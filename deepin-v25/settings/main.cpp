// 天气壁纸设置窗口：改 /etc/wwp/weather.json（位置、底层壁纸、模式、卡片），壁纸页面每 2 秒看一次配置，保存后马上生效。
#include "backend.h"

#include <QGuiApplication>
#include <QIcon>
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

    // 用 deepin 系统的 Chameleon 风格（DTK 控件，和控制中心一个样子）。界面里 import 了 org.deepin.dtk，
    // 没装 DTK 就起不来，所以不做 Fusion 兜底；QT_QUICK_CONTROLS_STYLE 可以指定别的风格调试
    if (qEnvironmentVariableIsEmpty("QT_QUICK_CONTROLS_STYLE"))
        QQuickStyle::setStyle(QStringLiteral("Chameleon"));

    Backend backend;
    QQmlApplicationEngine engine;
    engine.rootContext()->setContextProperty(QStringLiteral("backend"), &backend);
    QObject::connect(&engine, &QQmlApplicationEngine::objectCreationFailed, &app, [] { QCoreApplication::exit(1); }, Qt::QueuedConnection);
    engine.loadFromModule("WwpSettings", "Main");
    return app.exec();
}
