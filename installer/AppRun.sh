#!/bin/sh
# AppImage 入口脚本。
# linuxdeploy 会把 out/publish 打包成 AppDir，AppRun 是 AppDir 的启动入口。
# 这里解析软链（AppImage 运行时通过软链挂载），然后 exec 主程序。

HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/Sheas-Cealer-Nix" "$@"