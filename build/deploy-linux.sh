#!/usr/bin/env bash
# Linux 完整部署脚本
# 用法: bash build/deploy-linux.sh [安装目录]
# 默认安装到 ~/opt/Sheas-Cealer-Nix

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
APP_NAME="Sheas-Cealer-Nix"
DEST="${1:-$HOME/opt/$APP_NAME}"
PUBLISH_DIR="$PROJECT_DIR/out/publish-linux"
RID="linux-x64"

# 证书备份目录（跨部署保留）
CERT_BACKUP="$PROJECT_DIR/out/.cealing-certs-linux"
mkdir -p "$CERT_BACKUP"

# 需要保留的文件
KEEP_FILES=(Cealing-Root.pem Cealing-Key.pem Cealing-Cert.pem Cealing-Host-L.json Cealing-Host-U.json)

echo "==> 1/4 发布 GUI + agent"
dotnet publish "$PROJECT_DIR/Sheas-Cealer-Nix.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH_DIR" 2>&1
dotnet publish "$PROJECT_DIR/Cealing-Agent/Cealing-Agent.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH_DIR" 2>&1

# 下载 mihomo（如果本地没有）
MihomoFound=false
for path in /usr/local/bin/mihomo /opt/mihomo/mihomo "$HOME/.local/bin/mihomo"; do
    if [[ -f "$path" ]]; then
        cp "$path" "$PUBLISH_DIR/Cealing-Mihomo"
        cp "$path" "$PUBLISH_DIR/Cealing-Comihomo"
        chmod +x "$PUBLISH_DIR/Cealing-Mihomo" "$PUBLISH_DIR/Cealing-Comihomo"
        echo "  mihomo 已从本地复制: $path"
        MihomoFound=true
        break
    fi
done

if [[ "$MihomoFound" == false ]]; then
    echo "  本地未找到 mihomo，尝试下载..."
    ARCH=$(uname -m)
    case "$ARCH" in
        x86_64|amd64) MIHOMO_ARCH="linux-amd64" ;;
        aarch64|arm64) MIHOMO_ARCH="linux-arm64" ;;
        armv7l)        MIHOMO_ARCH="linux-armv7" ;;
        *)             MIHOMO_ARCH="linux-amd64" ;;
    esac
    MIHOMO_VERSION="${MIHOMO_VERSION:-1.19.31}"
    # mihomo 的 linux 资产只有 .gz（简单 gzip），没有 .tar.gz
    MIHOMO_ASSET="github.com/MetaCubeX/mihomo/releases/latest/download/mihomo-${MIHOMO_ARCH}-v${MIHOMO_VERSION}.gz"
    MihomoGz="$PUBLISH_DIR/mihomo-download.gz"

    # 直连 GitHub release 实测只有 ~25KB/s，所以默认就带镜像；GITHUB_MIRRORS 可覆盖
    MIRRORS="${GITHUB_MIRRORS:-https://gh-proxy.org https://ghfast.top https://ghproxy.net https://gh-proxy.com}"
    ok=false
    for base in $MIRRORS ""; do
        MihomoUrl="${base:+$base/}$MIHOMO_ASSET"
        echo "  下载: $MihomoUrl"
        rm -f "$MihomoGz"
        for attempt in 1 2 3; do
            # -C - 断点续传，避免大文件被截断
            curl -L --fail --connect-timeout 15 --max-time 300 -C - -o "$MihomoGz" "$MihomoUrl" 2>/dev/null || true
            # gzip -t 校验完整性
            if [[ -s "$MihomoGz" ]] && gzip -t "$MihomoGz" 2>/dev/null; then
                ok=true
                break
            fi
            echo "  第 $attempt 次下载不完整，重试..."
            sleep 5
        done
        [[ "$ok" == true ]] && break
    done

    if [[ "$ok" == true ]]; then
        echo "  解压..."
        gunzip -c "$MihomoGz" > "$PUBLISH_DIR/Cealing-Mihomo" 2>/dev/null
        if [[ -s "$PUBLISH_DIR/Cealing-Mihomo" ]]; then
            cp "$PUBLISH_DIR/Cealing-Mihomo" "$PUBLISH_DIR/Cealing-Comihomo"
            chmod +x "$PUBLISH_DIR/Cealing-Mihomo" "$PUBLISH_DIR/Cealing-Comihomo"
            echo "  mihomo 已下载 ($(du -h "$PUBLISH_DIR/Cealing-Mihomo" | cut -f1))"
        else
            echo "  mihomo 解压失败" >&2
        fi
    else
        echo "  mihomo 下载失败" >&2
        echo "  可设置 MIHOMO_VERSION 指定版本，或从 https://github.com/MetaCubeX/mihomo/releases 手动下载 mihomo-${MIHOMO_ARCH}-v*.gz" >&2
    fi
    rm -f "$MihomoGz" "$PUBLISH_DIR/mihomo"
fi

echo "==> 2/4 备份证书"
if [[ -d "$DEST" ]]; then
    for f in "${KEEP_FILES[@]}"; do
        [[ -f "$DEST/$f" ]] && cp -f "$DEST/$f" "$CERT_BACKUP/$f" && echo "  已备份: $f"
    done
fi

echo "==> 3/4 停旧进程"
pkill -f "Sheas-Cealer-Nix" 2>/dev/null || true
pkill -f "Cealing-Agent" 2>/dev/null || true
pkill -f "Cealing-Mihomo" 2>/dev/null || true
pkill -f "Cealing-Comihomo" 2>/dev/null || true
sleep 2

echo "==> 4/4 安装"
mkdir -p "$DEST"
cp -r "$PUBLISH_DIR/"* "$DEST/"
chmod +x "$DEST/Sheas-Cealer-Nix" "$DEST/Cealing-Agent" 2>/dev/null || true
chmod +x "$DEST/Cealing-Mihomo" "$DEST/Cealing-Comihomo" 2>/dev/null || true

# 恢复证书
for f in "${KEEP_FILES[@]}"; do
    [[ -f "$CERT_BACKUP/$f" ]] && cp -f "$CERT_BACKUP/$f" "$DEST/$f" && echo "  已恢复: $f"
done

# 创建桌面快捷方式（如果存在）
if [[ -d /usr/share/applications ]]; then
    DESKTOP_FILE="/usr/share/applications/sheas-cealer-nix.desktop"
    cat > "$DESKTOP_FILE" << EOF
[Desktop Entry]
Name=Sheas Cealer Nix
Comment=SNI 伪造工具
Exec=$DEST/Sheas-Cealer-Nix
Icon=$DEST/Sheas-Cealer-Nix-Logo.png
Terminal=false
Type=Application
Categories=Network;Security;
EOF
    echo "  已创建桌面快捷方式"
fi

# 创建符号链接到 PATH
if [[ -d "$HOME/.local/bin" ]]; then
    ln -sf "$DEST/Sheas-Cealer-Nix" "$HOME/.local/bin/sheas-cealer-nix"
    echo "  已创建符号链接: ~/.local/bin/sheas-cealer-nix"
fi

echo ""
echo "✓ 安装完成"
echo "  安装目录: $DEST"
echo "  运行: $DEST/Sheas-Cealer-Nix"
