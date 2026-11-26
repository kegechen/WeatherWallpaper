#!/bin/sh
# 把 build/ 里的产物打成 deb：wwp-weather-wallpaper_<版本>_<架构>.deb
#
#   ./deb/build-deb.sh                 普通用户运行（先跑过 install.sh build；要 dpkg-dev 里的 dpkg-shlibdeps）
#   sudo dpkg -i build/wwp-weather-wallpaper_*.deb
#
# 环境变量（CI 用，都可不填）：
#   DEB_VERSION     版本号，默认读 deb/version（没有就 0.1.0）
#   DEB_MAINTAINER  “名字 <邮箱>”，默认 GitHub 的 noreply 地址
#
# 装完之后：
#   * 控制中心 → 个性化 → 壁纸 → 动态壁纸 里出现“天气壁纸”（postinst 用 dpkg-divert 合并动态壁纸列表）。
#     只登记本包带的壁纸包；别的方式装进来的旧壁纸包（install.sh 装过的 weather 占位场景等）会被删掉
#   * 启动器里出现“天气壁纸设置”（位置、底层壁纸、卡片、模式）
#   * treeland 还在用旧 factory 时：sudo systemctl restart treeland，或结束 treeland-wallpaper-factory 进程再换一次壁纸
# 卸载 sudo dpkg -r wwp-weather-wallpaper：已登录用户正在用的天气壁纸换回系统默认壁纸，动态壁纸列表还原，
# /usr/local/share/wallpapers/wwp 整个删掉；/etc/wwp/weather.json 和 /var/lib/wwp 的自选壁纸保留，purge 才删。
set -eu
HERE=$(dirname "$(readlink -f "$0")")
ROOT=$(dirname "$HERE")
BUILD=$ROOT/build
STAGE=$BUILD/deb-root
VER=${DEB_VERSION:-$(cat "$HERE/version" 2>/dev/null || echo 0.1.0)}
ARCH=$(dpkg --print-architecture)
PKG=wwp-weather-wallpaper
META=/usr/share/wallpapers/deepin-livewallpapers/metadata.json
META_ORIG=$META.deepin
WWP_DIR=/usr/local/share/wallpapers/wwp

[ "$(id -u)" != 0 ] || { echo "请用普通用户打包"; exit 1; }
[ -x "$BUILD/treeland-wallpaper-factory" ] || { echo "先用普通用户运行 install.sh build"; exit 1; }
command -v dpkg-shlibdeps >/dev/null || { echo "缺 dpkg-shlibdeps：sudo apt install dpkg-dev"; exit 1; }

# 要打进去的壁纸包以源码 packages/ 为准（build/pkg 里可能还留着源码已删掉的旧包）
PKGS=
for m in "$ROOT"/packages/*/manifest.json; do
  [ -f "$m" ] || continue
  n=$(basename "$(dirname "$m")")
  [ -f "$BUILD/pkg/$n/$n.wwp" ] || { echo "缺 $BUILD/pkg/$n/$n.wwp：先用普通用户运行 install.sh build"; exit 1; }
  PKGS="$PKGS $n"
done
PKGS=${PKGS# }
[ -n "$PKGS" ] || { echo "packages/ 里没有壁纸包"; exit 1; }

rm -rf "$STAGE"

# ---- 数据树：和 install.sh install 装的完全一致 ----
install -D -m 755 "$BUILD/treeland-wallpaper-factory" "$STAGE/usr/local/bin/treeland-wallpaper-factory"
install -D -m 755 "$BUILD/wwp-set"                     "$STAGE/usr/local/bin/wwp-set"
install -D -m 755 "$BUILD/settings/wwp-settings"       "$STAGE/usr/local/bin/wwp-settings"
install -D -m 755 "$ROOT/settings/wwp-config"          "$STAGE/usr/local/libexec/wwp-config"
install -D -m 755 "$ROOT/tools/wwp-unset.sh"           "$STAGE/usr/local/libexec/wwp-unset"
install -D -m 644 "$ROOT/settings/io.github.wwp.weather.policy" "$STAGE/usr/share/polkit-1/actions/io.github.wwp.weather.policy"
install -D -m 644 "$ROOT/settings/wwp-settings.desktop"         "$STAGE/usr/share/applications/wwp-settings.desktop"
install -D -m 644 "$ROOT/settings/wwp-settings.png"             "$STAGE/usr/share/icons/hicolor/256x256/apps/wwp-settings.png"
install -d -m 755 "$STAGE$WWP_DIR"
for n in $PKGS; do
  cp -r "$BUILD/pkg/$n" "$STAGE$WWP_DIR/$n"
done
chmod -R u=rwX,go=rX "$STAGE$WWP_DIR"
# 默认配置（conffile：升级时本地改动会保留，卸载不删，purge 才删）
install -D -m 644 "$HERE/weather.json" "$STAGE/etc/wwp/weather.json"

# ---- 依赖 ----
# 动态库：dpkg-shlibdeps 只看二进制直接链接的库（ELF NEEDED），按各库包的 shlibs/symbols 给出带版本的包名。
# （不用 ldd：它列的是整棵依赖树，会把 libmpv 的几百个间接依赖、本机第三方软件自带的库都写进来）
SHL=$BUILD/shlibdeps
rm -rf "$SHL"
mkdir -p "$SHL/debian"
printf 'Source: %s\n\nPackage: %s\nArchitecture: any\n' "$PKG" "$PKG" > "$SHL/debian/control"
# 它往上找带 DEBIAN/ 的目录当包根（找不到每个文件警告一次），先建出来
mkdir -p "$STAGE/DEBIAN"
SHLIB_DEPS=$(cd "$SHL" && dpkg-shlibdeps -O \
  "$STAGE/usr/local/bin/treeland-wallpaper-factory" \
  "$STAGE/usr/local/bin/wwp-set" \
  "$STAGE/usr/local/bin/wwp-settings" | sed -n 's/^shlibs:Depends=//p')
[ -n "$SHLIB_DEPS" ] || { echo "dpkg-shlibdeps 没算出依赖"; exit 1; }
rm -rf "$SHL"
# QML 模块是运行时按 import 加载的，dpkg-shlibdeps 看不到，按 QML 文件里的 import 手写：
#   factory 的 Image/Video.qml、设置窗口：QtQuick、QtQuick.Controls、QtQuick.Dialogs、QtQuick.Layouts；网页版壁纸包：QtWebEngine
QML_DEPS="qml6-module-qtquick, qml6-module-qtquick-window, qml6-module-qtquick-controls, qml6-module-qtquick-templates, qml6-module-qtquick-dialogs, qml6-module-qtquick-layouts, qml6-module-qtqml-workerscript, qml6-module-qtwebengine"
# treeland：factory 由它拉起。这个 factory 是从 treeland 0.11 的 wallpaper-factory 改出来的、和它的私有壁纸协议绑在一起，
#   限定 0.11.x：treeland 出 0.12 时 apt 会先挡住升级（或要求先卸掉本包），而不是升级完壁纸全黑
# python3：提权助手 wwp-config；pkexec：设置窗口保存时提权
DEPS="treeland (>= 0.11~), treeland (<< 0.12~), python3, pkexec, $QML_DEPS, $SHLIB_DEPS"

# 不取本机 git 的 user.email：deb 会被传到公开的 Release，别把开发者的邮箱带进去
MAINT=${DEB_MAINTAINER:-"kegechen <kegechen@users.noreply.github.com>"}

# ---- 控制文件 ----
mkdir -p "$STAGE/DEBIAN"
cat > "$STAGE/DEBIAN/control" <<EOF
Package: $PKG
Version: $VER
Architecture: $ARCH
Maintainer: $MAINT
Depends: $DEPS
Recommends: qml6-module-qtquick-controls2-styles-chameleon
Section: x11
Priority: optional
Homepage: https://github.com/kegechen/WeatherWallpaper
Description: 天气动态壁纸（deepin 25 treeland 扩展）
 桌面和锁屏的实时天气动画壁纸：天空、云、雨雪、日月随真实天气变化；
 可叠在自选壁纸上（半透明天空）、极简模式、卡片内容和位置可调。
 附图形设置程序（启动器搜“天气壁纸”）和命令行 wwp-set。
EOF
cat > "$STAGE/DEBIAN/conffiles" <<EOF
/etc/wwp/weather.json
EOF
# deepin-wallpapers 升级时它的新列表落到 $META_ORIG（被 divert 了），触发本包 postinst 重新合并一次
cat > "$STAGE/DEBIAN/triggers" <<EOF
interest-noawait /usr/share/wallpapers/deepin-livewallpapers
EOF

cat > "$STAGE/DEBIAN/postinst" <<EOF
#!/bin/sh
set -e
META="$META"
META_ORIG="$META_ORIG"
WWP_DIR="$WWP_DIR"
PKGS="$PKGS"
install -d -m 755 /var/lib/wwp
# 不是本包带的壁纸包（install.sh 装过的 weather 占位场景、weather-overlay 变体等）删掉，
# 免得控制中心里出现好几个天气壁纸。本包旧版本带、新版本不带的，dpkg 升级时已删掉文件，剩下的空目录这里一并清掉
# 删之前把正在用旧包的已登录用户换到本包的第一个壁纸包上，不然桌面直接变空
FIRST=\${PKGS%% *}
if [ -d "\$WWP_DIR" ]; then
  for d in "\$WWP_DIR"/*/; do
    [ -d "\$d" ] || continue
    n=\$(basename "\$d")
    case " \$PKGS " in *" \$n "*) continue ;; esac
    WWP_MATCH="\$WWP_DIR/\$n/" /usr/local/libexec/wwp-unset "\$WWP_DIR/\$FIRST/\$FIRST.wwp" || true
    rm -rf "\${WWP_DIR:?}/\$n"
    echo "已删除旧壁纸包 \$n"
  done
fi
# 动态壁纸列表：dpkg-divert 把 deepin-wallpapers 的原列表挪开，生成“原列表 + 本包的壁纸包”
if [ "\$(dpkg-divert --truename "\$META")" != "\$META_ORIG" ]; then
  dpkg-divert --local --rename --divert "\$META_ORIG" --add "\$META"
fi
python3 -I - "\$META_ORIG" "\$META" "\$WWP_DIR" \$PKGS <<'PY'
import json, os, sys
orig, out, root = sys.argv[1:4]
items = []
if os.path.isfile(orig):
    with open(orig, encoding='utf-8') as f:
        items = json.load(f)
items = [i for i in items if not str(i.get('id', '')).startswith('wwp-')]
for n in sys.argv[4:]:
    d = os.path.join(root, n)
    entry = os.path.join(d, n + '.wwp')
    if os.path.isfile(entry):
        # path / thumbnail 写绝对路径：控制中心用 QDir::absoluteFilePath 拼接，绝对路径会原样保留
        items.append({"id": "wwp-" + n, "thumbnail": os.path.join(d, "thumbnail.png"),
                      "packageName": "", "path": entry})
os.makedirs(os.path.dirname(out), exist_ok=True)   # 没装 deepin-wallpapers 的机器（如 CI 容器）没有这个目录
tmp = out + '.tmp'
with open(tmp, 'w', encoding='utf-8') as f:
    json.dump(items, f, ensure_ascii=False, indent=4)
    f.write('\n')
os.chmod(tmp, 0o644)
os.replace(tmp, out)
PY
if [ "\$1" = configure ]; then
  echo "装好了：控制中心 → 个性化 → 壁纸 → 动态壁纸 里选“天气壁纸”；启动器里的“天气壁纸设置”可调位置、底层壁纸和卡片。"
  echo "treeland 还在用旧 factory 的话：sudo systemctl restart treeland，或结束 treeland-wallpaper-factory 进程后再选一次壁纸。"
fi
exit 0
EOF

cat > "$STAGE/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
# 真卸载（不是升级）时，趁 wwp-set 还在，把已登录用户正在用的天气壁纸换回系统默认壁纸
if [ "$1" = remove ] && [ -x /usr/local/libexec/wwp-unset ]; then
  /usr/local/libexec/wwp-unset || true
fi
exit 0
EOF

cat > "$STAGE/DEBIAN/postrm" <<EOF
#!/bin/sh
set -e
META="$META"
META_ORIG="$META_ORIG"
if [ "\$1" = remove ] || [ "\$1" = purge ]; then
  # 还原动态壁纸列表：删掉我们生成的，把 divert 走的原列表改名回去
  if [ "\$(dpkg-divert --truename "\$META")" = "\$META_ORIG" ]; then
    rm -f "\$META"
    dpkg-divert --local --rename --remove "\$META"
  fi
  rmdir "\$(dirname "\$META")" 2>/dev/null || true   # 只在是我们建的空目录时才删得掉
  # dpkg 只删自己装的文件：别的方式放进来的壁纸包、空目录一起清掉，控制中心和磁盘上都不留天气壁纸
  rm -rf "$WWP_DIR"
  rmdir /usr/local/share/wallpapers 2>/dev/null || true
fi
if [ "\$1" = purge ]; then
  rm -rf /var/lib/wwp /etc/wwp
fi
exit 0
EOF
chmod 755 "$STAGE/DEBIAN/postinst" "$STAGE/DEBIAN/prerm" "$STAGE/DEBIAN/postrm"

# ---- 打包 ----
DEB=$BUILD/${PKG}_${VER}_${ARCH}.deb
rm -f "$DEB"
dpkg-deb --build --root-owner-group "$STAGE" "$DEB"
echo "壁纸包：$PKGS"
echo "Depends: $DEPS"
echo "安装包：$DEB"
echo "安装：sudo dpkg -i $DEB"
