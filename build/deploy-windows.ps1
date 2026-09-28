#!/usr/bin/env pwsh
# Windows 完整部署脚本
# 用法: .\build\deploy-windows.ps1 [-InstallDir "C:\Path\To\Install"]

param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\Sheas-Cealer-Nix"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $ScriptDir
$AppName = "Sheas-Cealer-Nix"
$PublishDir = "$ProjectDir\out\publish-windows"
$Rid = "win-x64"

# 证书备份目录（跨部署保留）
$CertBackup = "$ProjectDir\out\.cealing-certs-win"
New-Item -ItemType Directory -Path $CertBackup -Force | Out-Null

# 需要保留的文件
$KeepFiles = @("Cealing-Root.pem", "Cealing-Key.pem", "Cealing-Cert.pem", "Cealing-Host-L.json", "Cealing-Host-U.json")

Write-Host "==> 1/4 发布 GUI + agent" -ForegroundColor Cyan

dotnet publish "$ProjectDir\Sheas-Cealer-Nix.csproj" -c Release -r $Rid --self-contained true -o $PublishDir 2>&1 | Out-Null
dotnet publish "$ProjectDir\Cealing-Agent\Cealing-Agent.csproj" -c Release -r $Rid --self-contained true -o $PublishDir 2>&1 | Out-Null

# 下载 mihomo（如果本地没有）
$MihomoSource = "C:\Program Files\mihomo\mihomo.exe"
if (Test-Path $MihomoSource) {
    Copy-Item $MihomoSource "$PublishDir\Cealing-Mihomo.exe" -Force
    Copy-Item $MihomoSource "$PublishDir\Cealing-Comihomo.exe" -Force
    Write-Host "  mihomo 已从本地复制" -ForegroundColor Green
} else {
    Write-Host "  本地未找到 mihomo，尝试下载..." -ForegroundColor Yellow
    $MihomoVersion = if ($env:MIHOMO_VERSION) { $env:MIHOMO_VERSION } else { "1.19.31" }
    $MihomoAsset = "github.com/MetaCubeX/mihomo/releases/latest/download/mihomo-windows-amd64-v$MihomoVersion.zip"
    $MihomoZip = "$PublishDir\mihomo-download.zip"

    # 直连 GitHub release 实测只有 ~25KB/s（21MB 资产跑不完），所以默认就带镜像；GITHUB_MIRRORS 可覆盖
    if ($env:GITHUB_MIRRORS) { $mirrors = $env:GITHUB_MIRRORS.Split(' ') | Where-Object { $_ } }
    else { $mirrors = @("https://gh-proxy.org", "https://ghfast.top", "https://ghproxy.net", "https://gh-proxy.com") }
    $bases = @($mirrors) + @("")

    $ok = $false
    foreach ($base in $bases) {
        $MihomoUrl = if ($base) { "$base/$MihomoAsset" } else { "https://$MihomoAsset" }
        Write-Host "  下载: $MihomoUrl"
        Remove-Item $MihomoZip -Force -ErrorAction SilentlyContinue
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            # -C - 断点续传，避免大文件被截断
            & curl.exe -L --fail --connect-timeout 15 --max-time 300 -C - -o $MihomoZip $MihomoUrl
            if ((Test-Path $MihomoZip) -and ((Get-Item $MihomoZip).Length -gt 0)) {
                Remove-Item "$PublishDir\mihomo-verify" -Recurse -Force -ErrorAction SilentlyContinue
                try {
                    # 用解压成功作为完整性校验
                    Expand-Archive -Path $MihomoZip -DestinationPath "$PublishDir\mihomo-verify" -Force
                    if ((Get-ChildItem "$PublishDir\mihomo-verify" -Recurse -File | Measure-Object).Count -gt 0) {
                        $ok = $true
                        break
                    }
                } catch {}
            }
            Write-Host "  第 $attempt 次下载不完整，重试..."
            Start-Sleep -Seconds 5
        }
        if ($ok) { break }
    }

    if ($ok) {
        Write-Host "  解压..." 
        Expand-Archive -Path $MihomoZip -DestinationPath "$PublishDir\mihomo-temp" -Force
        $MihomoExe = Get-ChildItem "$PublishDir\mihomo-temp" -Recurse -Filter "mihomo*.exe" | Select-Object -First 1
        if ($MihomoExe) {
            Copy-Item $MihomoExe.FullName "$PublishDir\Cealing-Mihomo.exe" -Force
            Copy-Item $MihomoExe.FullName "$PublishDir\Cealing-Comihomo.exe" -Force
            Write-Host "  mihomo 已下载" -ForegroundColor Green
        } else {
            Write-Host "  mihomo 可执行文件未找到" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  mihomo 下载失败" -ForegroundColor Red
        Write-Host "  可设置 MIHOMO_VERSION 指定版本，或从 https://github.com/MetaCubeX/mihomo/releases 手动下载 mihomo-windows-amd64-v*.zip" -ForegroundColor Yellow
    }
    Remove-Item $MihomoZip -Force -ErrorAction SilentlyContinue
    Remove-Item "$PublishDir\mihomo-temp", "$PublishDir\mihomo-verify" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "==> 2/4 备份证书" -ForegroundColor Cyan

if (Test-Path $InstallDir) {
    foreach ($file in $KeepFiles) {
        $source = Join-Path $InstallDir $file
        if (Test-Path $source) {
            Copy-Item $source $CertBackup -Force
            Write-Host "  已备份: $file"
        }
    }
}

Write-Host "==> 3/4 停旧进程" -ForegroundColor Cyan

$Processes = @("Sheas-Cealer-Nix", "Cealing-Agent", "Cealing-Mihomo", "Cealing-Comihomo")
foreach ($proc in $Processes) {
    Get-Process -Name $proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

Write-Host "==> 4/4 安装" -ForegroundColor Cyan

# 创建安装目录
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# 复制发布产物
Copy-Item "$PublishDir\*" $InstallDir -Recurse -Force

# 恢复证书
foreach ($file in $KeepFiles) {
    $backup = Join-Path $CertBackup $file
    if (Test-Path $backup) {
        Copy-Item $backup $InstallDir -Force
        Write-Host "  已恢复: $file"
    }
}

# 创建快捷方式
$ShortcutPath = "$env:USERPROFILE\Desktop\Sheas-Cealer-Nix.lnk"
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = Join-Path $InstallDir "Sheas-Cealer-Nix.exe"
$Shortcut.WorkingDirectory = $InstallDir
$Shortcut.Description = "Sheas Cealer Nix - SNI 伪造工具"
$Shortcut.Save()

Write-Host ""
Write-Host "✓ 安装完成" -ForegroundColor Green
Write-Host "  安装目录: $InstallDir"
Write-Host "  桌面快捷方式: $ShortcutPath"
Write-Host ""
Write-Host "运行: Start-Process $InstallDir\Sheas-Cealer-Nix.exe"
