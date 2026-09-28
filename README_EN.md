<h1 align="center">Sheas Cealer Nix</h1>
<h3 align="center">- Just Ceal It -</h3>
</br>

## Language
[中文 README](README.md)

## About
**Sheas Cealer Nix**: A SNI concealing tool based on **Avalonia(.Net8)**

* Applicable platform: Windows, Linux & MacOS (For other system, please refer to [Projects](https://github.com/SpaceTimee/Sheas-Cealer#Projects))
* Proxy engine: an installed system nginx is preferred; when no usable nginx is found it falls back to the built-in .NET engine. The choice can also be set manually in settings and is remembered across restarts
* Global cealing privilege model: consistent on all three platforms —— the app itself is never elevated; the first use asks for system authorization once (UAC on Windows, the authorization dialog on macOS, `pkexec` on Linux), only to launch a local privileged proxy process, and never asks again in the same session
* That privileged process edits hosts, installs a temporary root certificate and binds 80/443; everything is reverted when the app exits, and the certificate and hosts entries only exist while it is alive

## Privilege notes
When using "global cealing", Sheas Cealer Nix requests administrator authorization once on every platform (UAC on Windows, the system authorization dialog on macOS, `pkexec` on Linux). It is used for exactly three things:

1. Editing hosts (`%SystemRoot%\system32\drivers\etc\hosts` on Windows, `/etc/hosts` elsewhere) so the concealed domains point to your machine
2. Installing a temporary root certificate into the system trust store for local HTTPS decryption
3. Binding ports 80 / 443 to run the proxy engine

**How to revoke**: quitting the app terminates the privileged process and automatically reverts the hosts entries and the root certificate (on Windows / Linux the window X button only hides the app to the tray; use "退出" in the tray menu or `Ctrl+W` to really quit). To remove the certificate manually, run `sudo security delete-certificate -Z <SHA-1> /Library/Keychains/System.keychain` on macOS, or `certutil -delstore Root <SHA-1>` from an elevated prompt on Windows.

If the authorization is declined the proxy process ends up without administrator rights, and it then refuses to enable global cealing with an explicit message instead of silently writing hosts and doing nothing.

**Privacy**: the proxy only listens on the local loopback interface and never sends data back to any server. Related projects (such as Sheas Dop) are independent processes started separately.

## Mihomo engine
Official releases only ship Windows / Linux builds. On MacOS download one yourself and place it next to the `Sheas-Cealer-Nix` executable:
1. Download `mihomo-darwin-arm64-*.gz` (or `mihomo-darwin-amd64-*.gz`) from [MetaCubeX/mihomo Releases](https://github.com/MetaCubeX/mihomo/releases)
2. Rename the extracted binary to `Cealing-Mihomo` (**do not add a `.exe` suffix**)
3. `chmod +x Cealing-Mihomo` (the app also adds the execute bit automatically before launching)
4. `config.yaml` in the same directory is the base config template; the app injects `mixed-port`, `tun`, `dns` and `hosts` into it

The Mihomo engine requires root: both the generated `tun` (TUN device) and `dns.listen: ":53"` need administrator rights.

## MacOS build
Running the raw executable produced by `dotnet publish` gives the Dock a generic executable icon (it looks like a terminal window), because macOS only reads `Info.plist` / `CFBundleIconFile` from an `.app` bundle, while `<ApplicationIcon>` in the csproj only applies to Windows PE resources. Use the bundled script:

```bash
dotnet publish Sheas-Cealer-Nix.csproj -c Release -r osx-arm64 --self-contained true -o out/publish
dotnet publish Cealing-Agent/Cealing-Agent.csproj -c Release -r osx-arm64 --self-contained true -o out/publish
./build/make-macos-app.sh out/publish
open out/publish/Sheas-Cealer-Nix.app
```

**Both `publish` calls must output to the same directory**: the GUI looks for a sibling `Cealing-Agent` executable (relative to `ApplicationBase`) to launch the privileged root process, so both executables must sit side by side in `Contents/MacOS/`.

The script picks `Sheas-Cealer-Nix-Logo_512.png` as the icon source (falling back to `Sheas-Cealer-Nix-Logo.icns`), fills in any missing `@2x` sizes so the icon stays sharp on Retina, places the published files in `Contents/MacOS/` and the icon in `Contents/Resources/`, writes `Info.plist` and registers the bundle with LaunchServices.

The assemblies and `Cealing-Agent` must stay in `Contents/MacOS/` rather than `Resources/`, because the app resolves `nginx.conf`, `config.yaml`, `Cealing-Mihomo`, `Cealing-Agent` and friends relative to its own directory (`ApplicationBase`).

## Vocabulary
**[Sheas Cealer Dictionary](https://github.com/SpaceTimee/Sheas-Cealer/wiki/Sheas-Cealer-Dictionary)**

## Notes
1. The Built-in Cealing Host is continuously updated in the [Cealing Host repository](https://github.com/SpaceTimee/Cealing-Host)
2. When Sheas Cealer is updated, it will not overwrite the existing configs. If you need to synchronize with the upstream, you need to click the **Update Upstream Host** button, or **manually overwrite**
3. This project and all its resources are for the sole purpose of **defending against illegal network monitoring** and **conducting network security research**, and are not intended to bypass the censorship of any country
4. Please read the **Notes** and **Agreements** before use
5. Sheas Cealer is still in the **development stage**, but each production version will be **stable and available** before release
6. Github Release will retain **all versions** that can be used currently, but it is strongly recommended to use the **latest version**

## Agreements
1. [Privacy Policy](https://thoughts.teambition.com/share/6264eda98adeb10041b92fda#title=Sheas_Cealer_隐私政策)
2. [EULA](https://thoughts.teambition.com/share/6264edd78adeb10041b92fdb#title=Sheas_Cealer_使用协议)

## Download
Github: [https://github.com/SpaceTimee/Sheas-Cealer/releases](https://github.com/SpaceTimee/Sheas-Cealer/releases)

## Installation
Zip Package: Download Sheas Cealer Nix.zip and unzip -> Then you can use it directly

## Documentation
**[Sheas Cealer Documentation](https://github.com/SpaceTimee/Sheas-Cealer/wiki/Sheas-Cealer-Documentation)**

## Build
[Sheas Cealer Build](https://github.com/SpaceTimee/Sheas-Cealer/wiki/Sheas-Cealer-Build)

A bare `dotnet publish` output is not runnable on its own: mihomo is a runtime asset and must sit next to `Sheas-Cealer-Nix` as `Cealing-Mihomo` (plus a `Cealing-Comihomo` copy for the coproxy variant), which `build/deploy-windows.ps1` and `build/deploy-linux.sh` do for you. Without it the "启动全局伪造" and "编辑 Mihomo 配置" buttons stay greyed out.

## Principles
Using the startup parameter feature of the Chromium kernel to conceal SNI. For more detailes, please refer to [this article](https://nicebowl.fun/24_8)

## Credits
* **kit: Provides all the principle foundations for this project**
* **NiceBowl: Provides detailed principle explanations for this project**

## Developer
**Space Time**

## 联系方式
1. **QQ 群: 1034315671，716266896，338919498**
2. TG 群: [PixCealerChat](https://t.me/PixCealerChat)
3. **邮箱: Zeus6_6@163.com**

## Projects
1. [Sheas Cealer](https://github.com/SpaceTimee/Sheas-Cealer): Sheas Cealer for Windows
2. [Sheas Cealer Droid](https://github.com/SpaceTimee/Sheas-Cealer-Droid): Sheas Cealer for Android
3. [Cealing Host](https://github.com/SpaceTimee/Cealing-Host): The latest Built-in Cealing Host
4. [Sheas Dop](https://github.com/SpaceTimee/Sheas-Dop): DNS anti-pollution resolution tool (Sheas Cealer Global Cealing subproject)
5. [Sheas Nginx](https://github.com/SpaceTimee/Sheas-Nginx): Pixiv Nginx launcher (Sheas Cealer Global Purging × Pixiv Nginx cooperative subproject)
6. [Bot CealingCat](https://github.com/SpaceTimee/Bot-CealingCat): Telegram Bot providing Sheas Cealer related services
7. [Console HostChecker](https://github.com/SpaceTimee/Console-HostChecker): Cealing Host automated checking script
8. [Console HostGenerator](https://github.com/SpaceTimee/Console-HostGenerator): Cealing Host automated generation script

## License
[![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FSpaceTimee%2FSheas-Cealer.svg?type=large)](https://app.fossa.com/projects/git%2Bgithub.com%2FSpaceTimee%2FSheas-Cealer?ref=badge_large)

•ᴗ•
