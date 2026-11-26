// 实验：在 treeland 上用 Qt WebEngine（QML 的 WebEngineView）跑 web/，看能不能走 GPU、CPU 多少。
//   webtest <url> [--dump-gpu] [--size WxH]
//   --dump-gpu  打开后把页面文字（chrome://gpu 的内容）打印到 stdout 然后退出
#include <QGuiApplication>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickView>
#include <QtWebEngineQuick/qtwebenginequickglobal.h>

int main(int argc, char *argv[])
{
    QtWebEngineQuick::initialize();
    QGuiApplication app(argc, argv);

    QString url = QStringLiteral("chrome://gpu");
    bool dumpGpu = false;
    QSize size(1440, 960);
    for (int i = 1; i < argc; ++i) {
        const QString a = QString::fromLocal8Bit(argv[i]);
        if (a == QLatin1String("--dump-gpu")) {
            dumpGpu = true;
        } else if (a == QLatin1String("--size") && i + 1 < argc) {
            const QStringList p = QString::fromLocal8Bit(argv[++i]).split(QLatin1Char('x'));
            if (p.size() == 2)
                size = QSize(p[0].toInt(), p[1].toInt());
        } else {
            url = a;
        }
    }

    QQuickView view;
    // 带 scheme 的照原样用：fromUserInput 会把 file:// 里的 ?query 当成文件名的一部分转义掉
    QUrl startUrl(url);
    if (startUrl.scheme().isEmpty())
        startUrl = QUrl::fromUserInput(url);
    view.rootContext()->setContextProperty(QStringLiteral("startUrl"), startUrl);
    view.rootContext()->setContextProperty(QStringLiteral("dumpGpu"), dumpGpu);
    QObject::connect(view.engine(), &QQmlEngine::quit, &app, &QGuiApplication::quit);
    view.setResizeMode(QQuickView::SizeRootObjectToView);
    view.setSource(QUrl(QStringLiteral("qrc:/main.qml")));
    view.resize(size);
    view.show();
    return app.exec();
}
