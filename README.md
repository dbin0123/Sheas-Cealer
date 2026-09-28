<h1 align="center">Sheas Cealer Nix</h1>
<h3 align="center">- Just Ceal It -</h3>
</br>

## 其他语言
[English README](README_EN.md)

## 自我介绍
**Sheas Cealer Nix**: 一只基于 **Avalonia(.Net8)** 的 SNI 伪造工具

* 适用平台: Windows & Linux & MacOS (其他平台请参考[相关项目](https://github.com/SpaceTimee/Sheas-Cealer#相关项目))
* 反代引擎: 优先使用系统已安装的 nginx；没有可用 nginx 时自动回退到内置的 .NET 反代，也可在设置中手动指定（该选择会被记住）
* 全局伪造权限模型: 三个平台一致 —— 应用自身不提权，首次启用时弹一次系统授权框（Windows 用 UAC，macOS 用系统授权框，Linux 用 `pkexec`），授权只用于拉起一个本地特权代理进程，之后不再重复询问
* 该特权代理进程负责写入 hosts、安装临时根证书、监听 80/443；应用退出时全部还原，根证书与 hosts 条目只在进程存活期间存在

## 权限说明
在三个平台使用「全局伪造」时，Sheas Cealer Nix 都会请求一次管理员授权（Windows 使用 UAC，macOS 使用系统授权框，Linux 使用 `pkexec`）。授权只用于做三件事：

1. 修改 hosts（Windows 为 `%SystemRoot%\system32\drivers\etc\hosts`，其余平台为 `/etc/hosts`），把伪造域名指向本机
2. 安装一张临时根证书到系统信任库，用于本机 HTTPS 解密
3. 监听 80 / 443 端口，运行反代引擎

**撤销方式**：退出应用即结束该特权代理进程，hosts 条目与根证书会被自动还原（Windows / Linux 上点窗口 X 只是收进托盘，要用托盘菜单的「退出」或 `Ctrl+W` 才是真退出）。若需手动清理根证书：macOS 执行 `sudo security delete-certificate -Z <SHA-1> /Library/Keychains/System.keychain`；Windows 以管理员身份执行 `certutil -delstore Root <SHA-1>`。

授权被拒时特权代理进程拿不到管理员权限，此时它会直接拒绝启用全局伪造并给出提示，不会静默地写了 hosts 却没生效。

**隐私**：代理只监听本机回环地址，不向任何服务器回传数据。相关项目（如 Sheas Dop）是相互独立、各自启动的进程。

## Mihomo 引擎
官方 Release 只提供 Windows / Linux 构建，MacOS 请自行下载后放入程序目录：
1. 从 [MetaCubeX/mihomo Releases](https://github.com/MetaCubeX/mihomo/releases) 下载 `mihomo-darwin-arm64-*.gz`（或 `mihomo-darwin-amd64-*.gz`）
2. 解压后重命名为 `Cealing-Mihomo`（**不要加 `.exe`**），放在 `Sheas-Cealer-Nix` 可执行文件同级目录
3. `chmod +x Cealing-Mihomo`（程序在启动前也会自动补上执行位）
4. 同目录下的 `config.yaml` 为 Mihomo 基础配置模板，程序会在此基础上注入 `mixed-port`、`tun`、`dns` 与 `hosts`

Mihomo 引擎需要 root：配置中的 `tun`（TUN 设备）与 `dns.listen: ":53"` 都要求管理员权限。

## MacOS 构建
直接跑 `dotnet publish` 出来的裸可执行文件时，Dock 只能显示一个通用可执行文件图标（看起来像命令行窗口），因为 macOS 只认 `.app` 包里的 `Info.plist` 与 `CFBundleIconFile`；csproj 里的 `<ApplicationIcon>` 是给 Windows PE 资源用的，对 macOS 无效。用仓库里的脚本打包即可：

```bash
dotnet publish Sheas-Cealer-Nix.csproj -c Release -r osx-arm64 --self-contained true -o out/publish
dotnet publish Cealing-Agent/Cealing-Agent.csproj -c Release -r osx-arm64 --self-contained true -o out/publish
./build/make-macos-app.sh out/publish
open out/publish/Sheas-Cealer-Nix.app
```

**两次 `publish` 必须输出到同一个目录**：GUI 启动时按 `ApplicationBase` 找同级的 `Cealing-Agent` 可执行文件来拉起 root 代理进程，所以两个可执行文件必须在 `Contents/MacOS/` 里并排。

脚本会做三件事：
1. 取 `Sheas-Cealer-Nix-Logo_512.png` 作为图源，没有则退回 `Sheas-Cealer-Nix-Logo.icns`，并补齐缺失的 `@2x` 尺寸，Retina 下才不会发虚
2. 把发布产物放进 `Contents/MacOS/`，`AppIcon.icns` 放进 `Contents/Resources/`，并生成 `Info.plist`
3. 注册到 LaunchServices

`.dll` 与 `Cealing-Agent` 必须留在 `Contents/MacOS/` 而不是 `Resources/`，因为应用按自身所在目录（`ApplicationBase`）寻找 `nginx.conf`、`config.yaml`、`Cealing-Mihomo`、`Cealing-Agent` 等文件。

## 词汇解释
**[Sheas Cealer Dictionary](https://github.com/SpaceTimee/Sheas-Cealer/wiki/Sheas-Cealer-Dictionary)**

## 注意事项
1. 内置伪造规则在 [Cealing Host 存储库](https://github.com/SpaceTimee/Cealing-Host) 持续更新
2. Sheas Cealer Nix 更新时不会覆盖已有的伪造规则，如需与上游同步，需点击**更新上游规则**按钮，或**手动修改覆盖**
3. 本项目及所有相关资源仅供**抵御网络非法监听**和**开展网络安全研究**使用，无意绕过任何国家审查设备的审查
4. 为避免不必要的麻烦，使用前请先阅读注意事项和用户协议
5. Sheas Cealer Nix 仍处于**开发阶段**，但每个正式版发布前会尽量确保其**稳定可用**
6. Github Release 中会保留目前能够使用的**所有版本**，但强烈推荐使用**最新版**

## 用户协议
1. [隐私政策](https://thoughts.teambition.com/share/6264eda98adeb10041b92fdb#title=Sheas_Cealer_隐私政策)
2. [使用协议](https://thoughts.teambition.com/share/6264edd78adeb10041b92fdb#title=Sheas_Cealer_使用协议)

## 下载地址
Github: [https://github.com/SpaceTimee/Sheas-Cealer/releases](https://github.com/SpaceTimee/Sheas-Cealer/releases)

## 安装方式
Zip 压缩包: 下载 Sheas Cealer Nix.zip 并解压 -> 完成后即可直接使用

## 食用文档
**[Sheas Cealer Documentation](https://github.com/SpaceTimee/Sheas-Cealer/wiki/Sheas-Cealer-Documentation)**

## 项目构建
在线文档: [Sheas Cealer Build](https://github.com/SpaceTimee/Sheas-Cealer/wiki/Sheas-Cealer-Build)，macOS 见上文 **MacOS 构建** 一节。

### Windows 构建
```powershell
# 发布到 out/publish-windows
dotnet publish Sheas-Cealer-Nix.csproj -c Release -r win-x64 --self-contained true -o out/publish-windows
dotnet publish Cealing-Agent/Cealing-Agent.csproj -c Release -r win-x64 --self-contained true -o out/publish-windows

# 或使用部署脚本
.\build\deploy-windows.ps1
.\build\deploy-windows.ps1 -InstallDir "C:\Custom\Path"
```

部署脚本会：
1. 发布 GUI 和 agent 到同一目录
2. 复制 mihomo（本地没有就走镜像下载，镜像列表见 `GITHUB_MIRRORS`）
3. 备份并恢复证书（跨部署保留）
4. 创建桌面快捷方式

**只跑上面两条 `dotnet publish` 出来的目录不能直接用**：mihomo 是运行期资产，必须和 `Sheas-Cealer-Nix.exe` 同级放一份 `Cealing-Mihomo.exe`（coproxy 变体再放一份 `Cealing-Comihomo.exe`）。缺了它，GUI 上「启动全局伪造」「编辑 Mihomo 配置」会整排变灰。

### Linux 构建
```bash
# 发布到 out/publish-linux
dotnet publish Sheas-Cealer-Nix.csproj -c Release -r linux-x64 --self-contained true -o out/publish-linux
dotnet publish Cealing-Agent/Cealing-Agent.csproj -c Release -r linux-x64 --self-contained true -o out/publish-linux

# 或使用部署脚本
bash build/deploy-linux.sh
bash build/deploy-linux.sh /opt/Sheas-Cealer-Nix  # 自定义安装目录
```

部署脚本会：
1. 发布 GUI 和 agent 到同一目录
2. 复制 mihomo（本地没有就走镜像下载）
3. 备份并恢复证书（跨部署保留）
4. 创建桌面快捷方式（.desktop 文件）
5. 创建符号链接到 ~/.local/bin

## 项目原理
利用 Chromium 内核的启动参数特性伪造 SNI 拓展标记，详细原理可参考[这篇文章](https://nicebowl.fun/24_8)

## 致谢名单
* **kit: 为本项目提供全部的原理基础**
* **NiceBowl: 为本项目提供详细的原理说明**

## 开发者
**Space Time**

## 联系方式
1. **QQ 群: 1034315671，716266896，338919498**
2. TG 群: [PixCealerChat](https://t.me/PixCealerChat)
3. **邮箱: Zeus6_6@163.com**

## 相关项目
1. [Sheas Cealer](https://github.com/SpaceTimee/Sheas-Cealer): Sheas Cealer Windows 端
2. [Sheas Cealer Droid](https://github.com/SpaceTimee/Sheas-Cealer-Droid): Sheas Cealer 安卓端
3. [Cealing Host](https://github.com/SpaceTimee/Cealing-Host): 最新的 Sheas Cealer 内置伪造规则
4. [Sheas Dop](https://github.com/SpaceTimee/Sheas-Dop): DNS 抗污染解析工具 (Sheas Cealer 全局净化子项目)
5. [Sheas Nginx](https://github.com/SpaceTimee/Sheas-Nginx): Pixiv Nginx 启动器 (Sheas Cealer 全局伪造 × Pixiv Nginx 合作子项目)
6. [Bot CealingCat](https://github.com/SpaceTimee/Bot-CealingCat): 提供 Sheas Cealer 相关服务的 Telegram Bot
7. [Console HostChecker](https://github.com/SpaceTimee/Console-HostChecker): Cealing Host 自动化检查脚本
8. [Console HostGenerator](https://github.com/SpaceTimee/Console-HostGenerator): Cealing Host 自动化生成脚本

## 许可证
[![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSpaceTimee%2FSheas-Cealer.svg?type=large)](https://app.fossa.com/projects/git%2Bgithub.com%2FSpaceTimee%2FSheas-Cealer?ref=badge_large)

•ᴗ•
