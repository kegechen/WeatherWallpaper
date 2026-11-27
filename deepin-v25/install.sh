#!/bin/sh
# 天气壁纸的 treeland 扩展：编译、安装、卸载（deepin 25 / treeland 0.11）
#
#   ./install.sh build            普通用户运行：编译 factory 和 wwp-set，离线渲染壁纸包的缩略图和预览图（产物在 build/）
#   sudo ./install.sh install     装到 /usr/local，并把壁纸包登记进控制中心的“动态壁纸”列表
#   sudo ./install.sh uninstall   卸载。已登录用户正在用的天气壁纸会先换回系统默认壁纸（tools/wwp-unset.sh）
#
# 装好之后 treeland 还在用旧 factory，二选一让它换上新的：
#   1. sudo systemctl restart treeland（回到锁屏，重新解锁即可）
#   2. 结束旧 factory 进程，treeland 会按 PATH 重新拉起（/usr/local/bin 排在 /usr/bin 前面）；
#      之后要再切换一次壁纸（控制中心或 wwp-set），因为 treeland 0.11 不会把已在显示的壁纸挂到新进程的 surface 上。
#
# 壁纸包源码是 packages/<名字>/（manifest.json + QML），安装后是 /usr/local/share/wallpapers/wwp/<名字>/，
# 多出 thumbnail.png（控制中心列表缩略图）和 <名字>.wwp。登记给控制中心/treeland 的路径是 <名字>.wwp：
# factory 看到 .wwp 后缀就加载同目录的 manifest.json；而它本身是一张预览 PNG，控制中心显示“当前壁纸”时按图片读它。
set -eu
HERE=$(dirname "$(readlink -f "$0")")
BUILD=$HERE/build
PKG_SRC=$HERE/packages
PKG_DST=/usr/local/share/wallpapers/wwp
LIVE=/usr/share/wallpapers/deepin-livewallpapers
META=$LIVE/metadata.json
META_ORIG=$LIVE/metadata.json.deepin
BIN=/usr/local/bin
CFG=/etc/wwp/weather.json
# 设置窗口：wwp-settings 以用户身份运行，保存时 pkexec 提权助手 wwp-config 写配置、存自选壁纸（polkit 让本机活动会话免密码）
HELPER=/usr/local/libexec/wwp-config
# 卸载前把正在用的天气壁纸换回默认（deb 的 prerm 也调它）
UNSET=/usr/local/libexec/wwp-unset
# 壁纸停住（treeland 不再拉起 factory）时弹通知的用户服务，每个图形会话都启动
WATCH=/usr/local/libexec/wwp-watch
WATCH_UNIT=/usr/lib/systemd/user/wwp-watch.service
WATCH_LINK=/usr/lib/systemd/user/graphical-session.target.wants/wwp-watch.service
POLICY=/usr/share/polkit-1/actions/io.github.wwp.weather.policy
# .desktop 和图标装 /usr/share：polkit 只认 /usr/share/polkit-1；启动器（dde-application-manager）对 /usr/local/share 的索引不可靠，标准路径最稳
DESKTOP=/usr/share/applications/wwp-settings.desktop
ICON=/usr/share/icons/hicolor/256x256/apps/wwp-settings.png
IMG_DIR=/var/lib/wwp

need_root() {
  if [ "$(id -u)" != 0 ]; then echo "需要 root：sudo $0 $1"; exit 1; fi
}

# 列出 packages/ 下的壁纸包目录（有 manifest.json 的）
packages() {
  for p in "$PKG_SRC"/*/; do
    if [ -f "${p}manifest.json" ]; then printf '%s\n' "${p%/}"; fi
  done
}

# 把壁纸包源码目录 $1 拼进输出目录 $2：
#   base     包里只放和另一个包不同的文件（如只改 manifest 的变体），内容是那个包的名字，先拷它的
#   web.src  包要用仓库里别处的网页目录（网页版用 Windows 版的 web/），内容是相对本目录的路径，拷成包里的 web/
# 用位置参数不用变量：sh 的函数变量是全局的，递归会互相覆盖
assemble() {
  if [ -f "$1/base" ]; then assemble "$PKG_SRC/$(cat "$1/base")" "$2"; fi
  cp -r "$1/." "$2/"
  rm -f "$2/base" "$2/web.src"
  if [ -f "$1/web.src" ]; then
    rm -rf "$2/web"
    cp -r "$1/$(cat "$1/web.src")" "$2/web"
  fi
}

do_build() {
  if [ "$(id -u)" = 0 ]; then echo "请用普通用户编译"; exit 1; fi
  cmake -S "$HERE/factory" -B "$BUILD" -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo
  cmake --build "$BUILD"
  # 在 build/pkg/<名字>/ 拼出完整的壁纸包，install 原样拷过去。先整个清掉：
  # 源码里删掉的包（如旧的 weather 占位场景）不能留在这里，deb/build-deb.sh 也从这里取
  rm -rf "$BUILD/pkg"
  for p in $(packages); do
    n=$(basename "$p")
    out=$BUILD/pkg/$n
    rm -rf "$out"
    mkdir -p "$out"
    assemble "$p" "$out"
    # 控制中心自带的缩略图是 252x162
    "$BUILD/treeland-wallpaper-factory" --snapshot "$out" "$out/thumbnail.png" 1260x810 252x162
    "$BUILD/treeland-wallpaper-factory" --snapshot "$out" "$out/$n.wwp" 1920x1280
    echo "壁纸包 $n：$out（缩略图 thumbnail.png、预览 $n.wwp）"
  done
}

# 控制中心只读 $META。它属于 deepin-wallpapers 包，所以先 dpkg-divert 走原文件，再生成“原列表 + 我们的壁纸包”。
# deepin-wallpapers 升级后新列表会落到 $META_ORIG，重新运行 install（或 register）合并一次即可。
do_register() {
  need_root register
  if [ "$(dpkg-divert --truename "$META")" != "$META_ORIG" ]; then
    dpkg-divert --local --rename --divert "$META_ORIG" --add "$META"
  fi
  python3 - "$META_ORIG" "$META" "$PKG_DST" <<'PY'
import json, os, sys
orig, out, root = sys.argv[1:4]
with open(orig, encoding='utf-8') as f:
    items = json.load(f)
items = [i for i in items if not str(i.get('id', '')).startswith('wwp-')]
for n in sorted(os.listdir(root)) if os.path.isdir(root) else []:
    d = os.path.join(root, n)
    entry = os.path.join(d, n + '.wwp')
    if os.path.isfile(entry):
        # path / thumbnail 写绝对路径：控制中心用 QDir::absoluteFilePath 拼接，绝对路径会原样保留
        items.append({"id": "wwp-" + n, "thumbnail": os.path.join(d, "thumbnail.png"),
                      "packageName": "", "path": entry})
tmp = out + '.tmp'
with open(tmp, 'w', encoding='utf-8') as f:
    json.dump(items, f, ensure_ascii=False, indent=4)
    f.write('\n')
os.chmod(tmp, 0o644)
os.replace(tmp, out)
print("动态壁纸列表：%d 项（%s）" % (len(items), out))
PY
}

do_install() {
  need_root install
  if [ ! -x "$BUILD/treeland-wallpaper-factory" ]; then echo "先用普通用户运行 $0 build"; exit 1; fi
  install -m 755 "$BUILD/treeland-wallpaper-factory" "$BIN/.treeland-wallpaper-factory.new"
  mv -f "$BIN/.treeland-wallpaper-factory.new" "$BIN/treeland-wallpaper-factory"
  install -m 755 "$BUILD/wwp-set" "$BIN/wwp-set"
  install -m 755 "$BUILD/settings/wwp-settings" "$BIN/wwp-settings"
  install -D -m 755 "$HERE/settings/wwp-config" "$HELPER"
  install -D -m 755 "$HERE/tools/wwp-unset.sh" "$UNSET"
  install -D -m 755 "$HERE/tools/wwp-watch.sh" "$WATCH"
  install -D -m 644 "$HERE/tools/wwp-watch.service" "$WATCH_UNIT"
  install -d -m 755 "$(dirname "$WATCH_LINK")"
  ln -sfn ../wwp-watch.service "$WATCH_LINK"
  install -D -m 644 "$HERE/settings/io.github.wwp.weather.policy" "$POLICY"
  install -D -m 644 "$HERE/settings/wwp-settings.desktop" "$DESKTOP"
  install -D -m 644 "$HERE/settings/wwp-settings.png" "$ICON"
  install -d -m 755 "$IMG_DIR"
  install -d -m 755 "$PKG_DST"
  # 早期版本把 .desktop 和图标装在 /usr/local/share：清掉残留，免得启动器里出现两个入口
  rm -f /usr/local/share/applications/wwp-settings.desktop
  rm -f /usr/local/share/icons/hicolor/256x256/apps/wwp-settings.png
  for p in $(packages); do
    n=$(basename "$p")
    gen=$BUILD/pkg/$n
    if [ ! -f "$gen/$n.wwp" ]; then echo "缺 $gen/$n.wwp，先用普通用户运行 $0 build"; exit 1; fi
    tmp=$PKG_DST/.$n.new
    rm -rf "$tmp"
    cp -r "$gen" "$tmp"
    chown -R root:root "$tmp"
    chmod -R u=rwX,go=rX "$tmp"
    rm -rf "${PKG_DST:?}/$n"
    mv "$tmp" "$PKG_DST/$n"
    echo "壁纸包：$PKG_DST/$n/$n.wwp"
  done
  # 源码里已经删掉的壁纸包（旧的 weather 占位场景、weather-overlay 变体）：一并删掉，控制中心也不再列出
  for d in "$PKG_DST"/*/; do
    [ -d "$d" ] || continue
    n=$(basename "$d")
    if [ ! -f "$PKG_SRC/$n/manifest.json" ]; then
      rm -rf "${PKG_DST:?}/$n"
      echo "已删除旧壁纸包 $n（如果它正被用作桌面或锁屏壁纸，请在控制中心换一个）"
    fi
  done
  # 网页版的配置（字段见 README）：没有就写一份默认的，已有的不动。backdrop 空 = 不叠加底层壁纸
  if [ ! -e "$CFG" ]; then
    install -d -m 755 "$(dirname "$CFG")"
    cat > "$CFG.tmp" <<'EOF'
{
    "city": "",
    "backdrop": "",
    "minimal": false,
    "playSeconds": 30,
    "show": "hilo,src,aqi,feel,hum,wind,hours,days,sun,upd"
}
EOF
    chmod 644 "$CFG.tmp"
    mv -f "$CFG.tmp" "$CFG"
    echo "默认配置：$CFG"
  fi
  do_register
  # 已登录的用户现在就（重新）启动提醒服务
  "$WATCH" --each-user daemon-reload
  "$WATCH" --each-user restart wwp-watch.service
  echo "已安装 $BIN/treeland-wallpaper-factory、$BIN/wwp-set、设置窗口 $BIN/wwp-settings（启动器里的“天气壁纸设置”）。"
  echo "让 treeland 换用新 factory 的办法见本脚本开头。"
}

do_uninstall() {
  need_root uninstall
  # 先趁 wwp-set 还在，把已登录用户正在用的天气壁纸换回默认，否则桌面和控制中心的“当前壁纸”预览会空着
  if [ -x "$BIN/wwp-set" ]; then WWP_SET=$BIN/wwp-set sh "$HERE/tools/wwp-unset.sh" || true; fi
  sh "$HERE/tools/wwp-watch.sh" --each-user stop wwp-watch.service
  rm -f "$BIN/treeland-wallpaper-factory" "$BIN/wwp-set" "$BIN/wwp-settings" "$HELPER" "$UNSET" "$POLICY" "$DESKTOP" "$ICON"
  rm -f "$WATCH" "$WATCH_LINK" "$WATCH_UNIT"
  rm -rf "$PKG_DST"
  if [ "$(dpkg-divert --truename "$META")" = "$META_ORIG" ]; then
    rm -f "$META"
    dpkg-divert --local --rename --remove "$META"
  fi
  echo "已卸载。treeland 下次拉起 factory 时会用回 /usr/bin 的原版。配置 $CFG 和自选壁纸 $IMG_DIR 保留，不要了可以手动删除。"
}

case "${1:-}" in
  build) do_build ;;
  install) do_install ;;
  register) do_register ;;
  uninstall) do_uninstall ;;
  *) sed -n '2,16p' "$0"; exit 2 ;;
esac
