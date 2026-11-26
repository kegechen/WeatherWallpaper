#include "backend.h"

#include <QBuffer>
#include <QColor>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QImage>
#include <QImageReader>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPainter>

namespace {
const QString kConfig = QStringLiteral("/etc/wwp/weather.json");
const QString kHelper = QStringLiteral("/usr/local/libexec/wwp-config");   // 和 polkit 策略里的 exec.path 一致
const QString kWwpSet = QStringLiteral("/usr/local/bin/wwp-set");
const QString kPkgRoot = QStringLiteral("/usr/local/share/wallpapers/wwp/");
const QString kPackage = kPkgRoot + QStringLiteral("weather-web/weather-web.wwp");
const QString kSystemWalls = QStringLiteral("/usr/share/wallpapers/deepin");
const QString kDefaultBackdrop = QStringLiteral("/usr/share/backgrounds/default_background.jpg");   // treeland 的默认壁纸，同 host.js
const int kMaxSide = 4096;   // 存进系统目录的图片最长边（壁纸按屏幕物理分辨率裁剪，4K 屏也够）
}

Backend::Backend(QObject *parent)
    : QObject(parent)
{
}

QString Backend::configPath() const
{
    return kConfig;
}

QString Backend::defaultBackdrop() const
{
    return kDefaultBackdrop;
}

QVariantMap Backend::load()
{
    m_loadError.clear();
    QFile f(kConfig);
    if (!f.exists())
        return {};
    if (!f.open(QIODevice::ReadOnly)) {
        m_loadError = tr("读不了 %1：%2").arg(kConfig, f.errorString());
        return {};
    }
    QJsonParseError err;
    const auto doc = QJsonDocument::fromJson(f.readAll(), &err);
    if (!doc.isObject()) {
        m_loadError = tr("%1 不是合法的 JSON（%2），保存时会整份重写").arg(kConfig, err.errorString());
        return {};
    }
    return doc.object().toVariantMap();
}

// 系统目录里所有人读得到的图片直接引用（factory 以 dde 用户运行，能读）；家目录等其余位置的复制一份：
// 一来 dde 读不了，二来用户以后挪走、删掉原图也不影响壁纸。
// 能直接引用时返回解析掉符号链接后的真实路径（检查的是它；家目录里指向 /usr 的链接 dde 进不去），否则返回空
QString Backend::referenceDirectly(const QString &path) const
{
    const QFileInfo fi(path);
    const QString real = fi.canonicalFilePath();
    if (real.isEmpty() || !real.startsWith(QStringLiteral("/usr/")))
        return {};
    if (!(QFileInfo(real).permissions() & QFileDevice::ReadOther))
        return {};
    for (QDir d = QFileInfo(real).absoluteDir();; ) {
        if (!(QFileInfo(d.absolutePath()).permissions() & QFileDevice::ExeOther))
            return {};
        if (d.isRoot() || !d.cdUp())
            break;
    }
    return real;
}

void Backend::save(const QVariantMap &cfgIn, const QString &image)
{
    if (m_save)
        return;
    QVariantMap cfg = cfgIn;
    QJsonObject req;
    if (!image.isEmpty()) {
        if (const QString real = referenceDirectly(image); !real.isEmpty()) {
            if (cfg.value(QStringLiteral("lastBackdrop")).toString() == image)
                cfg[QStringLiteral("lastBackdrop")] = real;
            cfg[QStringLiteral("backdrop")] = real;
        } else {
            // 以当前用户身份读图，转成 JPEG 交给 wwp-config：它以 root 运行，只收数据不按路径读文件
            QImageReader reader(image);
            reader.setAutoTransform(true);   // 照片的 EXIF 方向
            QImage img = reader.read();
            if (img.isNull()) {
                emit saved(false, tr("图片打不开：%1").arg(reader.errorString()), {});
                return;
            }
            if (qMax(img.width(), img.height()) > kMaxSide)
                img = img.scaled(kMaxSide, kMaxSide, Qt::KeepAspectRatio, Qt::SmoothTransformation);
            QImage rgb(img.size(), QImage::Format_RGB32);
            rgb.fill(QColor(0x5b, 0x64, 0x70));   // 透明的地方垫上和读不到图片时一样的底色
            {
                QPainter p(&rgb);
                p.drawImage(0, 0, img);
            }
            QByteArray jpg;
            QBuffer buf(&jpg);
            buf.open(QIODevice::WriteOnly);
            if (!rgb.save(&buf, "JPG", 92)) {
                emit saved(false, tr("图片转换失败"), {});
                return;
            }
            req[QStringLiteral("image")] = QString::fromLatin1(jpg.toBase64());
        }
    }
    req[QStringLiteral("config")] = QJsonObject::fromVariantMap(cfg);

    m_save = new QProcess(this);
    emit busyChanged();
    connect(m_save, &QProcess::finished, this, [this](int code, QProcess::ExitStatus status) {
        QProcess *p = m_save;
        m_save = nullptr;
        p->deleteLater();
        emit busyChanged();
        const QString err = QString::fromUtf8(p->readAllStandardError()).trimmed();
        if (status == QProcess::NormalExit && code == 0) {
            const auto doc = QJsonDocument::fromJson(p->readAllStandardOutput());
            emit saved(true, QString(), doc.object().toVariantMap());
        } else if (code == 126) {
            emit saved(false, tr("没有授权（取消了认证）"), {});
        } else if (code == 127) {
            emit saved(false, tr("认证没通过，或者 pkexec 出错%1").arg(err.isEmpty() ? QString() : QStringLiteral("：") + err), {});
        } else {
            emit saved(false, err.isEmpty() ? tr("wwp-config 出错（退出码 %1）").arg(code) : err, {});
        }
    });
    connect(m_save, &QProcess::errorOccurred, this, [this](QProcess::ProcessError e) {
        if (e != QProcess::FailedToStart || !m_save)
            return;
        m_save->deleteLater();
        m_save = nullptr;
        emit busyChanged();
        emit saved(false, tr("启动不了 pkexec（没装 polkit？）"), {});
    });
    m_save->start(QStringLiteral("pkexec"), { kHelper });
    if (m_save) {   // 启动失败时上面的 errorOccurred 可能已经把它清掉了
        m_save->write(QJsonDocument(req).toJson(QJsonDocument::Compact));
        m_save->closeWriteChannel();
    }
}

QStringList Backend::systemWallpapers() const
{
    QStringList out;
    const QDir d(kSystemWalls);
    const auto list = d.entryInfoList({ QStringLiteral("*.jpg"), QStringLiteral("*.jpeg"), QStringLiteral("*.png"), QStringLiteral("*.webp") },
                                      QDir::Files | QDir::Readable, QDir::Name | QDir::IgnoreCase);
    for (const auto &fi : list)
        out << fi.absoluteFilePath();
    return out;
}

QVariantMap Backend::wallpaperState()
{
    QProcess p;
    p.start(kWwpSet, {});
    if (!p.waitForFinished(3000) || p.exitStatus() != QProcess::NormalExit || p.exitCode() != 0) {
        p.kill();
        return { { QStringLiteral("ok"), false } };
    }
    // 每个屏幕两行 "  桌面\t类型\t路径" / "  锁屏\t类型\t路径"；多屏时都是天气壁纸才算
    bool desk = true, lock = true, any = false;
    for (const QString &line : QString::fromUtf8(p.readAllStandardOutput()).split(QLatin1Char('\n'))) {
        const auto f = line.trimmed().split(QLatin1Char('\t'));
        if (f.size() < 3)
            continue;
        const bool ours = f[2].startsWith(kPkgRoot) && f[2].endsWith(QStringLiteral(".wwp"));
        if (f[0] == QStringLiteral("桌面")) {
            desk = desk && ours;
            any = true;
        } else if (f[0] == QStringLiteral("锁屏")) {
            lock = lock && ours;
            any = true;
        }
    }
    return { { QStringLiteral("ok"), any }, { QStringLiteral("desktop"), any && desk }, { QStringLiteral("lockscreen"), any && lock } };
}

void Backend::useWeatherWallpaper()
{
    if (m_setWall)
        return;
    m_setWall = new QProcess(this);
    m_setWall->setProcessChannelMode(QProcess::MergedChannels);
    emit busyChanged();
    connect(m_setWall, &QProcess::finished, this, [this](int code, QProcess::ExitStatus status) {
        QProcess *p = m_setWall;
        m_setWall = nullptr;
        p->deleteLater();
        emit busyChanged();
        const bool ok = status == QProcess::NormalExit && code == 0;
        emit wallpaperSet(ok, ok ? QString() : QString::fromUtf8(p->readAll()).trimmed());
    });
    connect(m_setWall, &QProcess::errorOccurred, this, [this](QProcess::ProcessError e) {
        if (e != QProcess::FailedToStart || !m_setWall)
            return;
        m_setWall->deleteLater();
        m_setWall = nullptr;
        emit busyChanged();
        emit wallpaperSet(false, tr("找不到 %1").arg(kWwpSet));
    });
    m_setWall->start(kWwpSet, { kPackage });
}
