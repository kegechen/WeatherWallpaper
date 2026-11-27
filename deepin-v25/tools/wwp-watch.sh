#!/bin/sh
# 壁纸停住时提醒用户。装好后是 /usr/local/libexec/wwp-watch，由 systemd 用户服务 wwp-watch.service
# 在每个图形会话里常驻（tools/wwp-watch.service）。
#
# treeland 0.11 的 factory 异常退出（崩溃、被结束）后会马上重新拉起，但一次 treeland 运行里累计超过 10 次
# 就不再拉起（日志 "Wallpaper process restart limit reached"），计数只在 treeland 重启时清零。之后桌面和锁屏
# 停在最后一帧、换壁纸也不生效，用户看不出原因。这里每 30 秒看一眼：treeland 在、它下面却没有 factory
# 子进程，连续两次就弹通知。通知上的“重启 treeland”按钮走 systemd 的 polkit 授权（要输管理员密码），
# 会关掉所有窗口回到锁屏。
#
# 不误报：只在这次 treeland 运行里见过 factory（之后没了），或者 treeland 日志里查得到“到上限 / 拉不起来”
# 时才提示（设了 TREELAND_DISABLE_WALLPAPER 这种从来不拉起的不算）；每次 treeland 运行最多提示一次。
# 读 treeland 日志要在 adm 或 systemd-journal 组里，读不到也照样提示，只是说不出具体原因。
#
#   wwp-watch                          常驻检查（服务就是这么跑的）
#   WWP_WATCH_TEST=1 wwp-watch         马上弹一条测试通知，按钮只打印、不重启，然后退出
#   wwp-watch --each-user <参数...>    root 用：对每个已登录 treeland 的用户执行 systemctl --user <参数>
#                                      （deb 的 postinst/prerm、install.sh 装卸时启停服务）
INTERVAL=${WWP_WATCH_INTERVAL:-30}
# factory 本身（treeland 按 PATH 拉起，/usr/local/bin 的是本包的），不含带 --application-name=treeland-wallpaper-factory 的 Chromium 子进程
FACTORY_RE='^([^ ]*/)?treeland-wallpaper-factory( |$)'

if [ "${1:-}" = --each-user ]; then
  shift
  for sock in /run/user/*/treeland.socket; do
    [ -S "$sock" ] || continue
    d=${sock%/treeland.socket}
    u=$(getent passwd "${d##*/}" | cut -d: -f1)
    [ -n "$u" ] || continue
    timeout 10 runuser -u "$u" -- env XDG_RUNTIME_DIR="$d" DBUS_SESSION_BUS_ADDRESS="unix:path=$d/bus" \
      systemctl --user "$@" || true
  done
  exit 0
fi

# 这次 treeland 运行里停掉 factory 的原因（treeland 日志里的那一行，查不到就是空）
reason() {
  journalctl -q -b _PID="$1" -o cat --no-pager -n 1 \
    -g 'Wallpaper process (restart limit reached|failed to start)' 2>/dev/null || true
}

# 弹通知，等用户点按钮；点了“重启 treeland”时打印 restart
notify() {
  case "$1" in
    *'limit reached'*) why='壁纸进程异常退出的次数到了 treeland 的上限（10 次），treeland 不再自动拉起它。' ;;
    *'failed to start'*) why='treeland 拉不起壁纸进程。' ;;
    *) why='壁纸进程退出后 treeland 没有再拉起它。' ;;
  esac
  notify-send -a 天气壁纸 -i wwp-settings -u critical -h string:desktop-entry:wwp-settings \
    -A restart='重启 treeland' '天气壁纸停住了' \
    "${why}桌面和锁屏会停在当前画面，换壁纸也不生效。重启 treeland（所有窗口会关闭，回到锁屏）或重启电脑后恢复。" \
    2>/dev/null || true
}

if [ -n "${WWP_WATCH_TEST:-}" ]; then
  act=$(notify 'Wallpaper process restart limit reached after 11 abnormal exits')
  echo "按钮：${act:-（没点，通知关了或超时）}"
  exit 0
fi

tl=        # 正在看的 treeland 进程
seen=      # 这次 treeland 运行里见过 factory
miss=0     # 连续几次没看到 factory
told=      # 这次 treeland 运行已经提示过
npid=      # 等用户点按钮的后台子进程
while :; do
  cur=$(pgrep -o -x treeland || true)
  if [ "$cur" != "$tl" ]; then
    # treeland 换了一个（重启过，计数已清零）：从头看；上一个 treeland 的通知没人点就不管了
    if [ -n "$npid" ]; then kill "$npid" 2>/dev/null || true; fi
    tl=$cur seen= miss=0 told= npid=
  fi
  if [ -n "$tl" ] && [ -z "$told" ]; then
    if pgrep -P "$tl" -f "$FACTORY_RE" >/dev/null; then
      seen=1 miss=0
    else
      miss=$((miss + 1))
      if [ "$miss" -ge 2 ]; then
        why=$(reason "$tl")
        if [ -n "$seen" ] || [ -n "$why" ]; then
          echo "treeland $tl 下没有壁纸进程了：${why:-日志里没查到原因}"
          told=1
          (
            if [ "$(notify "$why")" = restart ]; then
              echo "用户点了重启 treeland"
              systemctl --no-block restart treeland.service
            fi
          ) &
          npid=$!
        fi
      fi
    fi
  fi
  sleep "$INTERVAL"
done
