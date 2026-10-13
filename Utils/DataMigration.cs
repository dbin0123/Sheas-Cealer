using Cealing_Core;
using System;
using System.IO;

namespace Sheas_Cealer_Nix.Utils;

/// <summary>
/// 把旧版写在 app bundle 里的运行时文件搬到数据目录。
/// </summary>
/// <remarks>
/// 旧版所有可变文件都在 <c>AppDomain.CurrentDomain.SetupInformation.ApplicationBase</c>
/// （macOS 上是 <c>.app/Contents/MacOS</c>），升级时整包替换会把它们一起删掉。
/// 这里做一次性的「旧文件优先」迁移：数据目录里还没有同名文件时才从 bundle 拷过来，
/// 之后数据目录就是唯一真相，升级不再影响用户配置。
/// </remarks>
internal static class DataMigration
{
    // 只迁这些。Cealing-Root/Cert/Key.pem 必须一起搬：根证书要复用，
    // 换了根就得重新装进钥匙串，旧根还受信任会导致链对不上（见 ProxyCertificateFactory）。
    private static readonly string[] MigratedFileNames =
    [
        "cealing-proxy.json",
        "cealing-agent.log",
        "Cealing-Root.pem",
        "Cealing-Cert.pem",
        "Cealing-Key.pem",
        "Cealing-Host-L.json",
        "Cealing-Host-U.json",
        "Cealing-Host.json",
        "nginx.conf",
        "config.yaml"
    ];

    // 应用自带、随版本走的只读规则文件。语义和上面那份清单相反：那几个是「数据目录里有了就不动」，
    // 这几个每次启动都从 bundle 整份覆盖回去。
    //
    // 为什么要单独一个文件：Cealing-Host-L.json 是用户自己的，Cealing-Host-U.json 由远程更新覆盖，
    // 内置规则塞进这两份里的任何一份，都会在用户编辑本地规则或更新上游规则时被冲掉。
    private static readonly string[] BundledFileNames =
    [
        "Cealing-Host-B.json"
    ];

    internal static void Run()
    {
        try
        {
            string dataDir = AppPaths.EnsureDataDir();
            string appDir = Consts.MainConst.AppDir;

            // nginx 要求 logs/ 与 temp/ 存在
            Directory.CreateDirectory(Consts.MainConst.NginxLogsPath);
            Directory.CreateDirectory(Consts.MainConst.NginxTempPath);

            Migrate(appDir, dataDir);
            RefreshBundled(appDir, dataDir);
        }
        catch (Exception ex)
        {
            // 迁移失败不能挡住启动：后续各功能自己会按需重建缺失文件
            Console.Error.WriteLine($"data migration failed: {ex.Message}");
        }
    }

    internal static void Migrate(string appDir, string dataDir)
    {
        Directory.CreateDirectory(dataDir);

        // 数据目录与 bundle 相同（例如直接从源码跑、或数据目录被指回 bundle）时跳过拷贝
        if (AppPaths.Normalize(appDir) == AppPaths.Normalize(dataDir))
            return;

        foreach (string name in MigratedFileNames)
            TryCopy(Path.Combine(appDir, name), Path.Combine(dataDir, name));

        // Cealing-Host-*.json 用户自建的规则也要搬（本地 Host-L 已在上面处理）
        foreach (string legacy in Directory.EnumerateFiles(appDir, "Cealing-Host-*.json"))
            TryCopy(legacy, Path.Combine(dataDir, Path.GetFileName(legacy)));
    }

    internal static void RefreshBundled(string appDir, string dataDir)
    {
        // 同 Migrate：bundle 就是数据目录时覆盖自己只会把刚读出来的字节再写回去
        if (AppPaths.Normalize(appDir) == AppPaths.Normalize(dataDir))
            return;

        foreach (string name in BundledFileNames)
        {
            string bundled = Path.Combine(appDir, name);
            string target = Path.Combine(dataDir, name);

            if (!File.Exists(bundled))
                continue;

            try
            {
                File.Copy(bundled, target, overwrite: true);
            }
            catch (Exception ex)
            {
                // 刷不动（数据目录只读、文件被别的实例占着）就用上一次那份内置规则，
                // 反正它只是少了几条域名，不该挡住启动。
                Console.Error.WriteLine($"bundled rule refresh skipped {name}: {ex.Message}");
            }
        }
    }

    private static void TryCopy(string legacy, string target)
    {
        // 只在目标不存在时搬，避免旧 bundle 的陈旧内容覆盖用户新数据
        if (File.Exists(target) || !File.Exists(legacy))
            return;

        try
        {
            File.Copy(legacy, target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"data migration skipped {Path.GetFileName(legacy)}: {ex.Message}");
        }
    }
}
