#!/bin/sh
# 卸载前由 root 调用（deb 的 prerm、install.sh uninstall），装好后是 /usr/local/libexec/wwp-unset：
# 已登录（有 treeland 会话）的用户，桌面或锁屏正在用天气壁纸的，换回系统默认壁纸。
# 不换的话卸载后 treeland 还记着 .wwp 路径，桌面和控制中心的“当前壁纸”预览都是空的。
#
#   wwp-unset [换成的壁纸]      默认 /usr/share/backgrounds/default_background.jpg；给 .wwp 就换成那个天气壁纸
#
# 环境变量 WWP_MATCH：只换路径以它开头的壁纸，默认整个 /usr/local/share/wallpapers/wwp/
# （deb 升级时删旧壁纸包前，用它把正在用旧包的换成新包）。
# 没登录的用户（只有 treeland 的 dconfig 里记着路径）处理不了，下次登录后在控制中心重选即可。
# 尽力而为：哪一步失败都只打印提示，退出码总是 0，不挡卸载。
WWP_SET=${WWP_SET:-/usr/local/bin/wwp-set}
WWP_DIR=${WWP_MATCH:-/usr/local/share/wallpapers/wwp/}
DEFAULT=${1:-/usr/share/backgrounds/default_background.jpg}
TAB=$(printf '\t')

[ -x "$WWP_SET" ] || exit 0
case "$DEFAULT" in
  *.wwp) KIND= ;;          # 天气壁纸按 video 类型设（wwp-set 的默认）
  *) KIND=--image ;;
esac
# 给的壁纸不在：退回系统默认壁纸，再不在就取 deepin 自带的第一张
if [ ! -e "$DEFAULT" ]; then
  DEFAULT=/usr/share/backgrounds/default_background.jpg
  KIND=--image
fi
if [ ! -e "$DEFAULT" ]; then
  DEFAULT=$(ls /usr/share/wallpapers/deepin/*.jpg 2>/dev/null | head -n 1)
  KIND=--image
  [ -n "$DEFAULT" ] || { echo "wwp-unset：没有可换回的默认壁纸，跳过"; exit 0; }
fi

# 以会话用户身份跑 wwp-set（treeland 按会话记壁纸）；timeout 防合成器没响应时卡住卸载
as_user() {
  timeout 10 runuser -u "$U_NAME" -- env XDG_RUNTIME_DIR="$U_DIR" WAYLAND_DISPLAY=treeland.socket "$WWP_SET" "$@"
}

for sock in /run/user/*/treeland.socket; do
  [ -S "$sock" ] || continue
  U_DIR=${sock%/treeland.socket}
  U_NAME=$(getent passwd "${U_DIR##*/}" | cut -d: -f1)
  [ -n "$U_NAME" ] || continue
  cur=$(as_user 2>/dev/null) || continue
  # wwp-set 不带路径时每个屏幕打印“  桌面<TAB>video<TAB>路径”“  锁屏<TAB>…”
  desk=; lock=
  printf '%s\n' "$cur" | grep -q "^  桌面${TAB}.*${TAB}$WWP_DIR" && desk=1
  printf '%s\n' "$cur" | grep -q "^  锁屏${TAB}.*${TAB}$WWP_DIR" && lock=1
  if [ -n "$desk" ] && [ -n "$lock" ]; then
    role=
  elif [ -n "$desk" ]; then
    role=--desktop
  elif [ -n "$lock" ]; then
    role=--lockscreen
  else
    continue
  fi
  # shellcheck disable=SC2086  # role、KIND 为空时不能传空参数
  if as_user $role $KIND "$DEFAULT" >/dev/null 2>&1; then
    echo "wwp-unset：用户 $U_NAME 的壁纸已换成 $DEFAULT"
  else
    echo "wwp-unset：用户 $U_NAME 的壁纸没换成，请在控制中心另选一张"
  fi
done
exit 0
