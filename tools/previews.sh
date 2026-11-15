#!/usr/bin/env bash
# Switch preview modes one by one and capture the wallpaper (WebView only, no desktop) into shots/<mode>/
# usage: tools/previews.sh clear rain heavy snow ...
ROOT="$(cd "$(dirname "$0")/.." && pwd -W 2>/dev/null || cd "$(dirname "$0")/.." && pwd)"
EXE="$ROOT/bin/WeatherWallpaper.exe"
for m in "$@"; do
  "$EXE" preview "$m"
  sleep 4
  "$EXE" capture "$ROOT/shots/$m"
  sleep 2
done
"$EXE" preview live
