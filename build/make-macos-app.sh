#!/usr/bin/env bash
# 把 dotnet publish 的产物打包成标准 macOS .app。
#
# 为什么需要这个：
#   1) csproj 里的 <ApplicationIcon> 只对 Windows PE 资源生效，macOS 不读；
#   2) 直接运行裸可执行文件时没有 CFBundleInfoDictionary，Dock 只能画一个
#      通用可执行文件图标（看起来就是命令行样式），App 图标不生效；
#   3) mihomo / nginx 等二进制在启动时按 ApplicationBase 找同级文件，
#      所以 .dll 们必须留在 Contents/MacOS 里，不能挪到 Resources。
#
# 用法：
#   dotnet publish Sheas-Cealer-Nix.csproj -c Release -r osx-arm64 --self-contained true -o out/publish
#   ./build/make-macos-app.sh out/publish [App 名称] [RID] [版本]
#
# RID 必须和 GUI 的 publish 架构一致：CI 里 osx-x64 的包如果 agent 用 osx-arm64 编，
# 在 Intel Mac 上会直接起不来。

set -euo pipefail

PUBLISH_DIR="${1:-}"
APP_NAME="${2:-Sheas-Cealer-Nix}"
RID="${3:-osx-arm64}"
APP_VERSION="${4:-1.0.0}"

if [[ -z "$PUBLISH_DIR" || ! -d "$PUBLISH_DIR" ]]; then
    echo "用法: $0 <publish 输出目录> [App 名称]" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

EXECUTABLE="$APP_NAME"
if [[ ! -f "$PUBLISH_DIR/$EXECUTABLE" ]]; then
    echo "错误: $PUBLISH_DIR/$EXECUTABLE 不存在，请先 dotnet publish" >&2
    exit 1
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

# ---------- 1. 准备 AppIcon.icns ----------
# 仓库里的 .icns 是唯一图源；csproj 的 <ApplicationIcon>(.ico) 只对 Windows PE 生效。
# 多个候选时取分辨率更高的那个：Sheas-Cealer-Nix-Logo_512.png(512) 优先于 Sheas-Cealer-Nix-Logo.icns(256)。
ICNS=""
for candidate in "$PROJECT_DIR/Sheas-Cealer-Nix-Logo_512.png" "$PROJECT_DIR/Sheas-Cealer-Nix-Logo.icns"; do
    if [[ -f "$candidate" ]]; then
        ICNS="$candidate"
        break
    fi
done

if [[ -z "$ICNS" ]]; then
    echo "错误: $PROJECT_DIR 下找不到 Sheas-Cealer-Nix-Logo_512.png 或 Sheas-Cealer-Nix-Logo.icns" >&2
    exit 1
fi

# ---------- 1b. 补齐 @2x 尺寸 ----------
# 手工做的图标往往只有 1x（16/32/48/128/256/512），缺 @2x。
# Dock 在 Retina 上按 2x 绘制，缺图只能靠放大，边缘会发虚。
# 这里统一生成 10 个标准条目：比现有图小的都是精确缩小，不损失画质；
# 只有 512@2x(1024) 需要从 512 放大，macOS 允许缺这一项（缺了会回退到 512）。
augment_icns() {
    local src="$1" out="$2"
    local set="$WORK_DIR/aug.iconset"
    local bitmap="$WORK_DIR/aug-source"

    rm -rf "$set" "$bitmap"
    mkdir -p "$set"

    # iconutil 只会读 .icns，读不了位图。位图源直接当"最大那张"用。
    local largest
    case "$src" in
        *.icns)
            iconutil -c iconset "$src" -o "$set" 2>/dev/null || return 1
            largest=$(for f in "$set"/*.png; do
                printf '%s\t%s\n' "$(sips -g pixelWidth "$f" 2>/dev/null | awk '/pixelWidth/{print $2}')" "$f"
            done | sort -n | tail -1 | cut -f2-)
            ;;
        *)
            cp "$src" "$bitmap" || return 1
            largest="$bitmap"
            ;;
    esac

    [[ -f "$largest" ]] || return 1

    local entry logical pixel name
    for entry in 16:16 16:32 32:32 32:64 128:128 128:256 256:256 256:512 512:512 512:1024; do
        logical="${entry%%:*}"
        pixel="${entry##*:}"
        name="icon_${logical}x${logical}.png"
        [[ "$pixel" != "$logical" ]] && name="icon_${logical}x${logical}@2x.png"
        [[ -f "$set/$name" ]] && continue
        sips -s format png -z "$pixel" "$pixel" "$largest" --out "$set/$name" >/dev/null 2>&1 || true
    done

    # 源图若是位图，上面已把最大尺寸也重写了一遍，清掉临时文件再打包
    rm -f "$bitmap"
    iconutil -c icns "$set" -o "$out" 2>/dev/null
}

BUNDLE_ICNS="$WORK_DIR/AppIcon.icns"
if ! augment_icns "$ICNS" "$BUNDLE_ICNS" || [[ ! -s "$BUNDLE_ICNS" ]]; then
    echo "错误: 无法从 $ICNS 生成 icns" >&2
    exit 1
fi

# 回读校验：确保产出的真是 icns，而不是换了扩展名的位图。
# 少了这一步，坏图标会被静默塞进 bundle，Dock 又变回通用图标。
if ! iconutil -c iconset "$BUNDLE_ICNS" -o "$WORK_DIR/verify.iconset" 2>/dev/null; then
    echo "错误: 生成的 AppIcon.icns 无法被 iconutil 解析" >&2
    exit 1
fi
rm -rf "$WORK_DIR/verify.iconset"

# ---------- 2a. 重新发布 Cealing-Agent ----------
# 必须在这里重编：Sheas-Cealer-Nix.csproj 只 Compile Remove 排除了 Cealing-Agent\**，
# 从不构建它，所以 out/publish 里的 Cealing-Agent 会一直是早上的陈旧产物。
# 后果是改了 agent 源码（SNI、HPACK 等）却毫无效果 —— 改的二进制根本没进包。
AGENT_DIR="$WORK_DIR/agent"
dotnet publish "$PROJECT_DIR/Cealing-Agent/Cealing-Agent.csproj" \
    -c Release -r "$RID" --self-contained true -o "$AGENT_DIR" >&2
# 只覆盖 agent 自己的产物，别用它清掉 GUI 的 Cealing-Core.dll
for f in "$AGENT_DIR"/Cealing-Agent "$AGENT_DIR"/Cealing-Agent.dll "$AGENT_DIR"/Cealing-Agent.deps.json "$AGENT_DIR"/Cealing-Agent.runtimeconfig.json; do
    [[ -f "$f" ]] && cp -f "$f" "$PUBLISH_DIR/"
done
chmod +x "$PUBLISH_DIR/Cealing-Agent"

# ---------- 2. 组装 .app ----------
# 必须在工作目录里暂存：.app 如果直接建在 publish 目录内，
# tar 读源目录时会把正在写入的 .app 一起读进去，陷入无限自我复制。
APP_DIR="$PUBLISH_DIR/$APP_NAME.app"
STAGE_DIR="$WORK_DIR/$APP_NAME.app"
mkdir -p "$STAGE_DIR/Contents/MacOS" "$STAGE_DIR/Contents/Resources"

tar -C "$PUBLISH_DIR" -cf - . | tar -C "$STAGE_DIR/Contents/MacOS" -xf -

cp "$BUNDLE_ICNS" "$STAGE_DIR/Contents/Resources/AppIcon.icns"

# ---------- 2b. 编译状态栏助手 ----------
# 独立 ObjC 进程，创建 NSStatusItem 菜单栏图标。
SWIFT_SRC="$PROJECT_DIR/build/StatusBarHelper.m"
SWIFT_OUT="$STAGE_DIR/Contents/MacOS/StatusBarHelper"
if [[ -f "$SWIFT_SRC" ]]; then
    clang -fobjc-arc -framework Cocoa -O2 "$SWIFT_SRC" -o "$SWIFT_OUT" || {
        echo "警告: StatusBarHelper 编译失败，菜单栏图标将不可用" >&2
    }
    chmod +x "$SWIFT_OUT" 2>/dev/null || true
fi

# ---------- 3. Info.plist ----------
cat > "$STAGE_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE</string>
    <key>CFBundleIdentifier</key>
    <string>io.github.projectsheascealer.nix</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$APP_VERSION</string>
    <key>CFBundleVersion</key>
    <string>$APP_VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
PLIST

plutil -lint "$STAGE_DIR/Contents/Info.plist" >/dev/null

# ---------- 4. 就位并输出 ----------
rm -rf "$APP_DIR"
mv "$STAGE_DIR" "$APP_DIR"

# 通知 LaunchServices 认这个新 bundle，否则图标可能要等一会儿才刷新。
# 这步偶尔会卡住，超时就放弃，不影响 .app 本身可用。
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
[[ -x "$LSREGISTER" ]] && ( "$LSREGISTER" -f "$APP_DIR" >/dev/null 2>&1 & ) || true

echo "已生成: $APP_DIR"
echo "图源:   $(basename "$ICNS")"
echo "图标:   $(stat -f%z "$APP_DIR/Contents/Resources/AppIcon.icns") bytes (含 @2x)"
echo "文件:   $(ls "$APP_DIR/Contents/MacOS" | wc -l | tr -d ' ') 个"
echo "运行:   open \"$APP_DIR\""
