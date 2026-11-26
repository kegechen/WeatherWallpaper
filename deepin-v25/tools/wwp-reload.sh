#!/bin/sh
# 开发用：换上新编的 factory 并让 treeland 挂上新 surface。以桌面用户在会话里运行（会用 sudo 结束 dde 用户的 factory）。
#
# treeland 0.11 在 factory 重启后只按路径比较，不会给正在显示的壁纸换成新进程的 surface，
# 所以重启 factory 之后先切到一张静态图，再切回原来的桌面/锁屏壁纸。
# 注意：每次结束 factory 都算一次“崩溃”，treeland 累计超过 10 次就不再拉起，要重启 treeland 才清零。
set -eu
TMP_IMAGE=${TMP_IMAGE:-/usr/share/wallpapers/deepin/deepin-default.jpg}

line_of() { wwp-set | grep -m1 "$1" || true; }
restore() {  # restore <角色行> <--desktop|--lockscreen>
  t=$(printf '%s' "$1" | cut -f2)
  s=$(printf '%s' "$1" | cut -f3)
  [ -n "$s" ] || return 0
  if [ "$t" = image ]; then wwp-set "$2" --image "$s"; else wwp-set "$2" "$s"; fi
}

DESK=$(line_of 桌面)
LOCK=$(line_of 锁屏)
echo "桌面：$(printf '%s' "$DESK" | cut -f3)"
echo "锁屏：$(printf '%s' "$LOCK" | cut -f3)"

# --switch-only：factory 已经换过了（比如自己 kill 过），只做“切走再切回”
if [ "${1:-}" != --switch-only ]; then
  OLD=$(pgrep -u dde -f 'bin/treeland-wallpaper-factory$' || true)
  if [ -n "$OLD" ]; then sudo kill $OLD; fi
  i=0
  NEW=
  while [ $i -lt 50 ]; do
    NEW=$(pgrep -u dde -f 'bin/treeland-wallpaper-factory$' || true)
    if [ -n "$NEW" ] && [ "$NEW" != "$OLD" ]; then break; fi
    sleep 0.2
    i=$((i + 1))
  done
  echo "factory：${OLD:-无} -> ${NEW:-没起来}"
  sleep 1
fi

wwp-set --image "$TMP_IMAGE" >/dev/null
sleep 2
restore "$DESK" --desktop >/dev/null
restore "$LOCK" --lockscreen >/dev/null
echo "已切回原壁纸"
