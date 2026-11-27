#!/bin/sh
# 开发用：换上新编的 factory 并让 treeland 挂上新 surface。以桌面用户在会话里运行（会用 sudo 给 dde 用户的 factory 发信号）。
#
# 本工程的 factory 收到 SIGHUP 会原地重启（exec 同一路径上的新程序，进程号不变），treeland 看不到退出，
# 不占它“一次运行里异常退出超过 10 次就不再拉起”的额度。正在跑的若还是 deepin 原版或不认 SIGHUP 的旧版，
# SIGHUP 会直接结束它，treeland 按 PATH 拉起新的，这一次照常计数。
# treeland 0.11 在 factory 重启后只按路径比较，不会给正在显示的壁纸换成新进程的 surface，
# 所以重启 factory 之后先切到一张静态图，再切回原来的桌面/锁屏壁纸。
set -eu
TMP_IMAGE=${TMP_IMAGE:-/usr/share/wallpapers/deepin/deepin-default.jpg}

line_of() { wwp-set | grep -m1 "$1" || true; }
restore() {  # restore <角色行> <--desktop|--lockscreen>
  t=$(printf '%s' "$1" | cut -f2)
  s=$(printf '%s' "$1" | cut -f3)
  [ -n "$s" ] || return 0
  if [ "$t" = image ]; then wwp-set "$2" --image "$s"; else wwp-set "$2" "$s"; fi
}
factory_pid() { pgrep -u dde -f 'bin/treeland-wallpaper-factory$' || true; }

DESK=$(line_of 桌面)
LOCK=$(line_of 锁屏)
echo "桌面：$(printf '%s' "$DESK" | cut -f3)"
echo "锁屏：$(printf '%s' "$LOCK" | cut -f3)"

# --switch-only：factory 已经换过了（比如自己 kill 过），只做“切走再切回”
if [ "${1:-}" != --switch-only ]; then
  OLD=$(factory_pid)
  if [ -n "$OLD" ]; then sudo kill -HUP $OLD; fi
  # 原地重启要等它收尾、exec、重新连上 treeland；被结束的要等 treeland 拉起新的
  sleep 2
  i=0
  NEW=
  while [ $i -lt 40 ]; do
    NEW=$(factory_pid)
    if [ -n "$NEW" ]; then break; fi
    sleep 0.2
    i=$((i + 1))
  done
  if [ -z "$NEW" ]; then
    echo "factory：${OLD:-无} -> 没起来（treeland 可能已到重启上限，见 README）"
  elif [ "$NEW" = "$OLD" ]; then
    echo "factory：$OLD 原地重启（不计入 treeland 的重启次数）"
  else
    echo "factory：${OLD:-无} -> $NEW（旧进程不认 SIGHUP，被 treeland 重新拉起，计一次）"
  fi
  sleep 1
fi

wwp-set --image "$TMP_IMAGE" >/dev/null
sleep 2
restore "$DESK" --desktop >/dev/null
restore "$LOCK" --lockscreen >/dev/null
echo "已切回原壁纸"
