#!/bin/bash
# AppImage 入口：定位自身所在目录后启动主程序。
# readlink -f 处理符号链接（用户常把 AppImage 软链到 ~/Applications）。
DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
exec "$DIR/Sheas-Cealer-Nix" "$@"
