// Copyright (C) 2026 UnionTech Software Technology Co., Ltd.
// SPDX-License-Identifier: Apache-2.0 OR LGPL-3.0-only OR GPL-2.0-only OR GPL-3.0-only

#include "loggings.h"
#include "treelandwallpapernotifierclient.h"
#include "mpvvideoitem.h"
#include "wallpaperwindow.h"
#include "wwpcontroller.h"

#include <QQmlContext>

#include <private/qquickanimatedimage_p.h>

#define TREELANDWALLPAPERPRODUCEV1VERSION 1
#define WALLPAPERSOURCE "wallpaperSource"
#define WWPCONTROLLER "wwpController"

// WWP：这个窗口是 .wwp 场景时返回它的控制器，否则 nullptr
static WwpController *wwpControllerOf(QQuickView *view)
{
    return view ? qobject_cast<WwpController *>(view->property(WWPCONTROLLER).value<QObject *>()) : nullptr;
}

static QSize maxScreenSize()
{
    QSize maxSize;
    int maxArea = 0;

    const auto screens = QGuiApplication::screens();
    for (QScreen *screen : screens) {
        const QSize size = screen->geometry().size();
        const int area = size.width() * size.height();
        if (area > maxArea) {
            maxArea = area;
            maxSize = size;
        }
    }
    return maxSize;
}

static qreal minScreenRefreshIntervalMs()
{
    qreal maxRefreshRate = 0.0;

    const auto screens = QGuiApplication::screens();
    for (QScreen *screen : screens) {
        maxRefreshRate = qMax(maxRefreshRate, screen->refreshRate());
    }

    if (maxRefreshRate <= 0.0)
        return 16; // 60 hz

    return 1000.0 / maxRefreshRate;
}

TreelandWallpaperNotifierClientV1::TreelandWallpaperNotifierClientV1()
    : QWaylandClientExtensionTemplate<TreelandWallpaperNotifierClientV1>(TREELANDWALLPAPERPRODUCEV1VERSION)
{
    connect(qApp, &QGuiApplication::screenAdded,
            this, &TreelandWallpaperNotifierClientV1::onScreenAdded);
    connect(qApp, &QGuiApplication::screenRemoved,
            this, &TreelandWallpaperNotifierClientV1::onScreenRemoved);
    const auto screens = QGuiApplication::screens();
    for (QScreen *screen : screens) {
        connect(screen, &QScreen::geometryChanged,
                this, &TreelandWallpaperNotifierClientV1::updateAllWallpaperViewSizes);
    }
}

TreelandWallpaperNotifierClientV1::~TreelandWallpaperNotifierClientV1()
{
    foreach (auto window, m_windows) {
            delete window;
    }
    m_windows.clear();

    // WWP：原版的 factory 从不正常退出，这里没走到过；SIGHUP 原地重启会析构它，没绑上协议对象（没连上 treeland）时不能 destroy
    if (object())
        destroy();
}

void TreelandWallpaperNotifierClientV1::instantiate()
{
    initialize();
}

void TreelandWallpaperNotifierClientV1::treeland_wallpaper_notifier_v1_add(uint32_t source_type, const QString &file_source)
{
    foreach (auto window, m_windows) {
        if (window->property(WALLPAPERSOURCE).toString() == file_source) {
            return;
        }
    }

    QQuickView *wallpaperWindow = new QQuickView;
    WallpaperWindow *window = WallpaperWindow::get(wallpaperWindow);
    window->setSource(file_source);
    wallpaperWindow->setResizeMode(QQuickView::SizeRootObjectToView);

    // WWP：路径以 .wwp 结尾、且是 root 装好的包，就加载包里的 QML 场景（treeland 按 video 类型发过来，不检查内容）。
    // 别处的 .wwp 不执行，按原版逻辑交给 mpv（.wwp 本身是预览 PNG，显示成静态图）
    bool wwp = WwpController::isWwpSource(file_source);
    QString untrusted;
    if (wwp && !WwpController::isTrustedSource(file_source, &untrusted)) {
        qCWarning(WALLPAPER) << "不加载壁纸包" << file_source << untrusted;
        wwp = false;
    }
    if (wwp) {
        auto *controller = new WwpController(file_source, wallpaperWindow);
        wallpaperWindow->setProperty(WWPCONTROLLER, QVariant::fromValue<QObject *>(controller));
        wallpaperWindow->rootContext()->setContextProperty(QStringLiteral("wallpaper"), controller);
        connect(controller,
                &WwpController::loaded,
                window, [window]() {
                    window->setLoaded(true);
                },
                Qt::SingleShotConnection);
        wallpaperWindow->loadFromModule("com.treeland.wallfactory", "WwpScene");
        if (!wallpaperWindow->rootObject()) {
            qCCritical(WALLPAPER) << "Failed to load WwpScene for" << file_source;
            delete wallpaperWindow;
            return;
        }
    } else
    switch (source_type) {

    case QtWayland::treeland_wallpaper_notifier_v1::
        wallpaper_source_type::wallpaper_source_type_image: {
        wallpaperWindow->loadFromModule("com.treeland.wallfactory", "Image");
        QObject *root = wallpaperWindow->rootObject();
        auto *image = qobject_cast<QQuickAnimatedImage *>(root);
        if (!image) {
            qCCritical(WALLPAPER)
            << "Root object is not QQuickAnimatedImage";
            delete wallpaperWindow;
            return;
        }
        connect(image,
                &QQuickAnimatedImage::statusChanged,
                window, [window](QQuickImageBase::Status status) {
                    if (status == QQuickImageBase::Ready)
                        window->setLoaded(true);
                });
        image->setSource(QUrl::fromLocalFile(file_source));
        break;
    }

    case QtWayland::treeland_wallpaper_notifier_v1::
        wallpaper_source_type::wallpaper_source_type_video: {
        wallpaperWindow->loadFromModule("com.treeland.wallfactory", "Video");
        QObject *root = wallpaperWindow->rootObject();
        auto *video = qobject_cast<MpvVideoItem *>(root);
        if (!video) {
            qCCritical(WALLPAPER)
            << "Root object is not MpvVideoItem";
            delete wallpaperWindow;
            return;
        }

        connect(video,
            &MpvVideoItem::fileLoaded,
            window,[window]() {
                window->setLoaded(true);
            },
            Qt::SingleShotConnection);
        video->setSource(file_source);
        break;
    }

    default:
        qCCritical(WALLPAPER) << "Unsupported wallpaper source type:"
                              << source_type;
        delete wallpaperWindow;
        return;
    }

    wallpaperWindow->setProperty(WALLPAPERSOURCE, file_source);
    wallpaperWindow->resize(maxScreenSize());
    wallpaperWindow->show();
    m_windows.append(wallpaperWindow);
    connect(window,
            &WallpaperWindow::playChanged,
            this,
            &TreelandWallpaperNotifierClientV1::onPlayChanged);
    connect(window,
            &WallpaperWindow::slowDownChanged,
            this,
            &TreelandWallpaperNotifierClientV1::onSlowDownChanged);
}

void TreelandWallpaperNotifierClientV1::treeland_wallpaper_notifier_v1_remove(const QString &file_source)
{
    foreach (auto window, m_windows) {
        if (window->property(WALLPAPERSOURCE).toString() == file_source) {
            m_windows.removeOne(window);
            delete window;

            return;
        }
    }
}

void TreelandWallpaperNotifierClientV1::updateAllRefreshInterval()
{
    int minRefreshInterval = minScreenRefreshIntervalMs();
    foreach(auto window, std::as_const(m_windows)) {
        QObject *root = window->rootObject();
        auto *video = qobject_cast<MpvVideoItem *>(root);
        if (!video) {
            continue;
        }

        video->setRefreshInterval(minRefreshInterval);
    }
}

void TreelandWallpaperNotifierClientV1::updateAllWallpaperViewSizes()
{
    const QSize size = maxScreenSize();
    if (!size.isValid())
        return;

    for (QQuickView *view : std::as_const(m_windows)) {
        if (!view)
            continue;

        view->resize(size);
    }
}

void TreelandWallpaperNotifierClientV1::onScreenAdded(QScreen *screen)
{
    connect(screen, &QScreen::geometryChanged,
            this, &TreelandWallpaperNotifierClientV1::updateAllWallpaperViewSizes);

    updateAllWallpaperViewSizes();
    updateAllRefreshInterval();
}

void TreelandWallpaperNotifierClientV1::onScreenRemoved(QScreen *screen)
{
    disconnect(screen, &QScreen::geometryChanged,
               this, &TreelandWallpaperNotifierClientV1::updateAllWallpaperViewSizes);

    updateAllWallpaperViewSizes();
    updateAllRefreshInterval();
}

void TreelandWallpaperNotifierClientV1::onPlayChanged(bool play)
{
    WallpaperWindow *send = static_cast<WallpaperWindow *>(sender());
    QQuickView *wallpaperWindow = static_cast<QQuickView *>(send->parentWindow());
    if (WwpController *wwp = wwpControllerOf(wallpaperWindow)) {
        wwp->setPlay(play);
        return;
    }
    QObject *root = wallpaperWindow->rootObject();
    auto *video = qobject_cast<MpvVideoItem *>(root);
    if (video) {
        video->setPause(!play);
    } else {
        if (QQuickAnimatedImage *image = qobject_cast<QQuickAnimatedImage *>(root)) {
            if (image->frameCount() > 1) {
                image->setPaused(!play);
            }
        } else {
            qCCritical(WALLPAPER) << "Unsupported wallpaper Object";
        }
    }
}

void TreelandWallpaperNotifierClientV1::onSlowDownChanged(uint32_t duration)
{
    WallpaperWindow *send = static_cast<WallpaperWindow *>(sender());
    QQuickView *wallpaperWindow = static_cast<QQuickView *>(send->parentWindow());
    if (WwpController *wwp = wwpControllerOf(wallpaperWindow)) {
        wwp->slowDown(duration);
        return;
    }
    QObject *root = wallpaperWindow->rootObject();
    auto *video = qobject_cast<MpvVideoItem *>(root);
    if (video) {
        video->slowDown(duration);
    } else {
        if (QQuickAnimatedImage *image = qobject_cast<QQuickAnimatedImage *>(root)) {
            if (image->frameCount() > 1) {
                image->setPaused(true);
            }
        } else {
            qCCritical(WALLPAPER) << "Unsupported wallpaper Object";
        }
    }
}
