// 天气壁纸设置窗口：改 /etc/wwp/weather.json（位置、底层壁纸、模式、卡片），壁纸页面每 2 秒看一次配置，保存后马上生效。
#include "backend.h"

#include <QFile>
#include <QGuiApplication>
#include <QIcon>
#include <QLibraryInfo>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickStyle>

int main(int argc, char *argv[])
{
    QGuiApplication app(argc, argv);
    app.setApplicationName(QStringLiteral("wwp-settings"));
    app.setApplicationDisplayName(QStringLiteral("天气壁纸设置"));
    app.setDesktopFileName(QStringLiteral("wwp-settings"));
    app.setWindowIcon(QIcon(QStringLiteral(":/qt/qml/WwpSettings/wwp-settings.png")));

    // deepin 上用系统的 Chameleon 风格（DTK 控件，和控制中心一个样子）；没有就用 Fusion。QT_QUICK_CONTROLS_STYLE 可以指定别的
    if (qEnvironmentVariableIsEmpty("QT_QUICK_CONTROLS_STYLE")) {
        const bool chameleon = QFile::exists(QLibraryInfo::path(QLibraryInfo::QmlImportsPath) + QStringLiteral("/Chameleon/qmldir"));
        QQuickStyle::setStyle(chameleon ? QStringLiteral("Chameleon") : QStringLiteral("Fusion"));
    }

    Backend backend;
    QQmlApplicationEngine engine;
    engine.rootContext()->setContextProperty(QStringLiteral("backend"), &backend);
    QObject::connect(&engine, &QQmlApplicationEngine::objectCreationFailed, &app, [] { QCoreApplication::exit(1); }, Qt::QueuedConnection);
    engine.loadFromModule("WwpSettings", "Main");
    return app.exec();
}
