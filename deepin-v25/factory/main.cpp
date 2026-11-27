// Copyright (C) 2026 UnionTech Software Technology Co., Ltd.
// SPDX-License-Identifier: Apache-2.0 OR LGPL-3.0-only OR GPL-2.0-only OR GPL-3.0-only

#include "treelandwallpapernotifierclient.h"
#include "wwpcontroller.h"

#include <DLog>

#include <QGuiApplication>
#include <QQmlContext>
#include <QQuickView>
#include <QQuickWindow>
#include <QSGRendererInterface>
#include <QSocketNotifier>
#include <QTimer>

#include <algorithm>
#include <cerrno>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

#include <dirent.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <unistd.h>

#ifdef WWP_HAVE_WEBENGINE
#include <QtWebEngineQuick/qtwebenginequickglobal.h>
#endif

DCORE_USE_NAMESPACE;

Q_DECLARE_LOGGING_CATEGORY(WWP)  // wwpcontroller.cpp

// WWP：收到 SIGHUP 就原地重启——正常收尾（关掉 Chromium 子进程、断开 Wayland），再 exec 同一路径的程序。
// 进程号不变、也没有退出，treeland 的 WallpaperLauncher 看不到这次重启，不记作异常退出：它一次运行里
// 累计异常退出超过 10 次就不再拉起 factory，壁纸冻住、只能重启 treeland。换上新装的 factory
// （tools/wwp-reload.sh）就走这条路；真崩溃、被 kill 照样计数，由 treeland 决定还拉不拉。
// 信号处理函数里只往 socketpair 写一个字节，事件循环里再处理。
static int s_hupFds[2] = { -1, -1 };

static void onSighup(int)
{
    const char c = 1;
    [[maybe_unused]] const ssize_t n = ::write(s_hupFds[0], &c, 1);
}

static void watchSighup(QGuiApplication &app, bool &restart)
{
    if (::socketpair(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC | SOCK_NONBLOCK, 0, s_hupFds) != 0) {
        qCWarning(WWP) << "wwp: socketpair 失败，SIGHUP 原地重启不可用";
        return;
    }
    auto *notifier = new QSocketNotifier(s_hupFds[1], QSocketNotifier::Read, &app);
    QObject::connect(notifier, &QSocketNotifier::activated, &app, [&app, &restart]() {
        char buf[16];
        while (::read(s_hupFds[1], buf, sizeof(buf)) > 0) { }
        qCInfo(WWP) << "wwp: 收到 SIGHUP，原地重启 factory";
        restart = true;
        app.quit();
    });
    struct sigaction sa = {};
    sa.sa_handler = onSighup;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_RESTART;
    ::sigaction(SIGHUP, &sa, nullptr);
}

// 原地重启时 Chromium 的子进程（QtWebEngineProcess）还没退：它们等父进程这头的通信 fd 关掉，这要到 exec
// 才发生（fd 带 CLOEXEC），exec 前怎么等都等不到。exec 之后新程序不认识它们，退出后就成了僵尸，一直挂到
// factory 退出（每重启一次多两个）。所以 exec 前先收掉已经退出的，还活着的子进程号记进 WWP_REAP_PIDS，
// 由新程序在事件循环里收（只 wait 这几个进程号，不碰 Chromium 自己的子进程）
static constexpr char kReapEnv[] = "WWP_REAP_PIDS";

static void handOverChildren()
{
    while (::waitpid(-1, nullptr, WNOHANG) > 0) { }

    const pid_t self = ::getpid();
    QByteArray pids;
    if (DIR *proc = ::opendir("/proc")) {
        while (const dirent *e = ::readdir(proc)) {
            char *end = nullptr;
            const long pid = std::strtol(e->d_name, &end, 10);
            if (pid <= 0 || *end != '\0')
                continue;
            char path[64];
            std::snprintf(path, sizeof(path), "/proc/%ld/stat", pid);
            FILE *f = std::fopen(path, "r");
            if (!f)
                continue;
            char buf[512];
            const size_t n = std::fread(buf, 1, sizeof(buf) - 1, f);
            std::fclose(f);
            buf[n] = '\0';
            // “pid (comm) state ppid …”，comm 里可能有空格和括号，从最后一个 ')' 往后读
            const char *p = std::strrchr(buf, ')');
            char state = 0;
            long ppid = 0;
            if (p && std::sscanf(p + 1, " %c %ld", &state, &ppid) == 2 && ppid == self) {
                if (!pids.isEmpty())
                    pids += ',';
                pids += QByteArray::number(qlonglong(pid));
            }
        }
        ::closedir(proc);
    }
    if (pids.isEmpty())
        ::unsetenv(kReapEnv);
    else
        ::setenv(kReapEnv, pids.constData(), 1);
}

// 新程序这边：取出上一轮交过来的进程号，并从环境里删掉（免得 Chromium 子进程继承）
static std::vector<pid_t> takeHandedOverChildren()
{
    std::vector<pid_t> pids;
    const QByteArray env = qgetenv(kReapEnv);
    qunsetenv(kReapEnv);
    for (const QByteArray &s : env.split(',')) {
        bool ok = false;
        const int pid = s.toInt(&ok);
        if (ok && pid > 0)
            pids.push_back(pid);
    }
    return pids;
}

// 每半秒收一次，30 秒后还没退的不管了
static void reapHandedOver(QObject *parent, std::vector<pid_t> pids)
{
    if (pids.empty())
        return;
    auto *timer = new QTimer(parent);
    timer->setInterval(500);
    QObject::connect(timer, &QTimer::timeout, timer, [timer, pids = std::move(pids), tries = 0]() mutable {
        pids.erase(std::remove_if(pids.begin(), pids.end(), [](pid_t pid) {
                       const pid_t r = ::waitpid(pid, nullptr, WNOHANG);
                       return r == pid || (r < 0 && errno == ECHILD);
                   }),
                   pids.end());
        if (pids.empty() || ++tries >= 60)
            timer->deleteLater();
    });
    timer->start();
}

// WWP：壁纸包可以用 WebEngineView，必须在建 QGuiApplication 之前初始化（只设共享 GL 上下文等属性，
// 真正的 Chromium 进程要等第一个 WebEngineView 出现才起，所以不用网页的壁纸不受影响）
static void initWebEngine()
{
#ifdef WWP_HAVE_WEBENGINE
    QtWebEngineQuick::initialize();
#endif
}

static QSize parseSize(const char *s, QSize fallback)
{
    const QStringList p = QString::fromLocal8Bit(s).split(QLatin1Char('x'));
    if (p.size() != 2)
        return fallback;
    const QSize size(p[0].toInt(), p[1].toInt());
    return size.isValid() ? size : fallback;
}

// WWP：离线渲染壁纸包的一帧（软件光栅 + offscreen，不连 Wayland），用来生成控制中心缩略图和自查画面
//   treeland-wallpaper-factory --snapshot <包目录> <输出文件> [渲染宽x高] [输出宽x高] [场景秒数]
// 输出一律存 PNG（不看后缀），这样可以直接生成 <名字>.wwp 预览图
static int runSnapshot(int argc, char *argv[])
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickWindow::setGraphicsApi(QSGRendererInterface::Software);
    initWebEngine();
    QGuiApplication app(argc, argv);

    const QString source = QString::fromLocal8Bit(argv[2]);
    const QString out = QString::fromLocal8Bit(argv[3]);
    const QSize renderSize = argc > 4 ? parseSize(argv[4], QSize(1440, 960)) : QSize(1440, 960);
    const QSize outSize = argc > 5 ? parseSize(argv[5], renderSize) : renderSize;
    const qreal at = argc > 6 ? QString::fromLocal8Bit(argv[6]).toDouble() : 8.0;

    QQuickView view;
    auto *controller = new WwpController(source, &view);
    controller->freezeAt(at);
    view.rootContext()->setContextProperty(QStringLiteral("wallpaper"), controller);
    view.setResizeMode(QQuickView::SizeRootObjectToView);
    view.loadFromModule("com.treeland.wallfactory", "WwpScene");
    if (!view.rootObject())
        return 1;
    view.resize(renderSize);
    view.show();

    // 等多久再截：manifest 的 snapshotDelay（毫秒），网页场景要等页面加载、宿主发完消息
    const int delay = std::clamp(controller->manifest().value(QStringLiteral("snapshotDelay"), 1000).toInt(), 200, 15000);
    int ret = 1;
    QTimer::singleShot(delay, &app, [&]() {
        QImage img = view.grabWindow();
        if (img.size() != outSize)
            img = img.scaled(outSize, Qt::IgnoreAspectRatio, Qt::SmoothTransformation);
        ret = img.save(out, "PNG") ? 0 : 1;
        app.quit();
    });
    app.exec();
    return ret;
}

// 原版的 main 主体。WWP：多了 SIGHUP 原地重启，restart 置位时返回后由 main 去 exec
static int runFactory(int argc, char *argv[], bool &restart, std::vector<pid_t> handedOver)
{
    // The entire rendering framework, including mpv, is based on OpenGL.
    // QQuickWindow::setGraphicsApi(QSGRendererInterface::OpenGL);
    initWebEngine();
    QGuiApplication app(argc, argv);

    app.setOrganizationName("deepin");
    app.setApplicationName("treeland-wallpaper-factory");

    DLogManager::registerJournalAppender();
    WwpController::keepInfoLogging();
    watchSighup(app, restart);
    reapHandedOver(&app, std::move(handedOver));

    std::unique_ptr<TreelandWallpaperNotifierClientV1> produce;
    produce.reset(new TreelandWallpaperNotifierClientV1);
    produce->instantiate();

    return app.exec();
}

int main(int argc, char *argv[])
{
    if (argc >= 4 && qstrcmp(argv[1], "--snapshot") == 0)
        return runSnapshot(argc, argv);

    // QGuiApplication 会从 argv 里摘掉它认识的参数，exec 用一份原样的
    std::vector<char *> args(argv, argv + argc);
    args.push_back(nullptr);

    bool restart = false;
    const int ret = runFactory(argc, argv, restart, takeHandedOverChildren());
    if (!restart)
        return ret;
    // QGuiApplication 已析构（mpv、Wayland 连接都收拾干净了）。argv[0] 是 treeland 按 PATH 找到的完整路径，
    // 装了新版就 exec 到新版上；环境变量（WAYLAND_DISPLAY 等）原样带过去
    handOverChildren();
    ::execvp(args[0], args.data());
    // exec 失败就按异常退出交给 treeland：它会重新拉起（记一次）
    perror("wwp: 原地重启 exec 失败");
    return 1;
}
