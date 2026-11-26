#pragma once

#include <QObject>
#include <QProcess>
#include <QStringList>
#include <QUrl>
#include <QVariantMap>

// 设置窗口的后台：读配置、经 pkexec + wwp-config 写配置、列系统壁纸、查 / 设当前壁纸（wwp-set）。
// 配置文件 /etc/wwp/weather.json 所有人可读，直接读；写要 root，交给 wwp-config（polkit 规则让本机活动会话免密码）。
class Backend : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool busy READ busy NOTIFY busyChanged)
    Q_PROPERTY(QString configPath READ configPath CONSTANT)
    Q_PROPERTY(QString defaultBackdrop READ defaultBackdrop CONSTANT)

public:
    explicit Backend(QObject *parent = nullptr);

    bool busy() const { return m_save || m_setWall; }
    QString configPath() const;
    QString defaultBackdrop() const;

    // 读配置：文件没有 = 空对象；坏了也返回空对象，原因在 loadError()
    Q_INVOKABLE QVariantMap load();
    Q_INVOKABLE QString loadError() const { return m_loadError; }
    // 保存整份配置。image = 新选的本地图片：系统目录里 dde 读得到的直接引用，其余（家目录等）转成 JPEG 交给 wwp-config 存到 /var/lib/wwp。
    // 结果由 saved 信号给出（cfg 是实际写进去的配置）
    Q_INVOKABLE void save(const QVariantMap &cfg, const QString &image);
    Q_INVOKABLE QStringList systemWallpapers() const;
    Q_INVOKABLE QUrl fileUrl(const QString &path) const { return QUrl::fromLocalFile(path); }
    Q_INVOKABLE QString localPath(const QUrl &url) const { return url.toLocalFile(); }
    // 桌面 / 锁屏现在是不是天气壁纸：{ ok, desktop, lockscreen }（ok = wwp-set 能用）
    Q_INVOKABLE QVariantMap wallpaperState();
    // 桌面和锁屏都设成天气壁纸（weather-web），结果由 wallpaperSet 信号给出
    Q_INVOKABLE void useWeatherWallpaper();

signals:
    void busyChanged();
    void saved(bool ok, const QString &message, const QVariantMap &cfg);
    void wallpaperSet(bool ok, const QString &message);

private:
    QString referenceDirectly(const QString &path) const;

    QProcess *m_save = nullptr;
    QProcess *m_setWall = nullptr;
    QString m_loadError;
};
