#!/usr/bin/env bash
# 完整部署：build → 打包 → 安装。
#
# 从 v1.0.14 起，运行时状态不再放在 .app 里，而是放在用户数据目录：
#   ~/Library/Application Support/Sheas-Cealer-Nix/
# 所以部署时整包替换 /Applications 下的 .app 不再丢任何东西，
# 证书/规则/日志天然存活。脚本仍保留一次「从旧 bundle 抢救」的动作，
# 用于把 v1.0.13 及更早版本写在 .app 里的状态搬到数据目录。
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
APP_NAME="Sheas-Cealer-Nix"
DEST="/Applications/$APP_NAME.app"
PUBLISH_DIR="$PROJECT_DIR/out/publish"
MACOS_DIR="$DEST/Contents/MacOS"
RID="osx-arm64"

# 运行时状态目录（与 AppPaths.DataDir 保持一致）
DATA_DIR="$HOME/Library/Application Support/$APP_NAME"
mkdir -p "$DATA_DIR"

# 旧版本把这些文件写在 .app bundle 里。整包替换会删掉，所以先抢救到数据目录。
# 只在数据目录还没有同名文件时搬，避免旧 bundle 的陈旧内容覆盖用户新数据。
# 应用自身启动时也会做同样的迁移（Utils/DataMigration.cs），这里只是让部署后
# 立刻可验证，不依赖用户先跑一次 GUI。
KEEP_STATE=(Cealing-Root.pem Cealing-Key.pem Cealing-Cert.pem Cealing-Host-L.json Cealing-Host-U.json nginx.conf config.yaml cealing-proxy.json)

if [[ -d "$MACOS_DIR" ]]; then
    for f in "${KEEP_STATE[@]}"; do
        if [[ -f "$MACOS_DIR/$f" && ! -f "$DATA_DIR/$f" ]]; then
            mv -f "$MACOS_DIR/$f" "$DATA_DIR/$f" 2>/dev/null \
                || cp -f "$MACOS_DIR/$f" "$DATA_DIR/$f" 2>/dev/null \
                || echo "  警告: 旧状态 $f 迁移失败（可能需要 sudo）" >&2
        fi
    done
fi

echo "==> 1/4 publish GUI + agent"
dotnet publish "$PROJECT_DIR/Sheas-Cealer-Nix.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH_DIR" >&2

# 下载 mihomo（如果本地没有）
MihomoFound=false
for path in /opt/homebrew/opt/mihomo/bin/mihomo /usr/local/bin/mihomo "$HOME/.local/bin/mihomo"; do
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
    if [[ "$ARCH" == "arm64" ]]; then
        MIHOMO_ARCH="darwin-arm64"
    else
        MIHOMO_ARCH="darwin-amd64"
    fi
    MIHOMO_VERSION="${MIHOMO_VERSION:-1.19.31}"
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
        # mihomo 的 .gz 资产是简单 gzip（不是 tar.gz）
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

# 自带的 OpenSSL 动态库会和应用用的 .NET 冲突，去掉
rm -f "$PUBLISH_DIR"/libcrypto.3.dylib "$PUBLISH_DIR"/libssl.3.dylib

echo "==> 2/4 打包 .app"
bash "$SCRIPT_DIR/make-macos-app.sh" "$PUBLISH_DIR" "$APP_NAME" >&2

echo "==> 3/4 停旧进程"
# agent 是 root，先走它自己的 shutdown 通道（SIGTERM 杀不掉）
SOCK=$(ls -t /var/folders/*/*/T/cealing-agent.sock 2>/dev/null | head -1 || true)
if [[ -n "${SOCK:-}" && -S "$SOCK" ]]; then
    python3 - "$SOCK" <<'PY' 2>/dev/null || true
import socket, sys, json
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.settimeout(10); s.connect(sys.argv[1])
s.sendall(json.dumps({"command": "shutdown"}).encode() + b"\n")
try: s.recv(4096)
except Exception: pass
s.close()
PY
fi
pkill -f "Cealing-Mihomo" 2>/dev/null || true
pkill -f "Cealing-Comihomo" 2>/dev/null || true
pkill -f "StatusBarHelper" 2>/dev/null || true
pkill -f "$APP_NAME" 2>/dev/null || true
sleep 2

echo "==> 4/4 安装"
# 注意：make-macos-app.sh 把 .app 直接建在 $PUBLISH_DIR 里，
# 所以这里**不能再 cp -R 那个 .app**（会把 .app 复制进自己，无限递归直到路径超长）。
# 正确做法是把 .app 移到暂存区再从暂存区装到 /Applications。
NEW_APP="$PUBLISH_DIR/$APP_NAME.app"
STAGE="$PUBLISH_DIR/.deploy-stage.$$"
rm -rf "$STAGE"
mkdir -p "$STAGE"
mv "$NEW_APP" "$STAGE/"

# 运行时数据在 $DATA_DIR，与 bundle 无关，rm -rf 整包替换不会影响它。

rm -rf "$DEST"
mv "$STAGE/$APP_NAME.app" "$DEST"
rmdir "$STAGE" 2>/dev/null || rm -rf "$STAGE"

chmod +x "$MACOS_DIR/Cealing-Agent" "$MACOS_DIR/Cealing-Mihomo" "$MACOS_DIR/Cealing-Comihomo" "$MACOS_DIR/StatusBarHelper" 2>/dev/null || true
rm -f "$MACOS_DIR"/libcrypto.3.dylib "$MACOS_DIR"/libssl.3.dylib 2>/dev/null || true

# 证书完整性检查。Root.pem 与 Key.pem 必须成对，缺一会导致 agent 重新生成根证书、
# 浏览器立刻拒 TLS（ERR_CONNECTION_CLOSED / unknown CA）。
if [[ ! -f "$DATA_DIR/Cealing-Root.pem" || ! -f "$DATA_DIR/Cealing-Key.pem" ]]; then
    echo "!! 警告: 数据目录里证书不完整，首次启动会生成新根证书，浏览器需要重新授权" >&2
fi
if [[ ! -f "$DATA_DIR/Cealing-Host-U.json" ]]; then
    echo "  提示: 上游规则 U 尚未下载，打开应用后点「更新上游规则」即可" >&2
fi

ROOT_SHA=$(openssl x509 -in "$DATA_DIR/Cealing-Root.pem" -noout -fingerprint -sha1 2>/dev/null | tr -d ':' | sed 's/.*=//' | tr 'a-f' 'A-F' || echo "-")
echo "已安装: $DEST"
echo "运行时数据: $DATA_DIR"
echo "根证书指纹: ${ROOT_SHA:-无（首次启动生成，需要重新授权）}"
echo "运行: open \"$DEST\""
