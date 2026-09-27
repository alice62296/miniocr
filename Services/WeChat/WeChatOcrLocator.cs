using System.Text;

namespace MiniOcr.Services;

public readonly record struct WeChatRegistryInstall(string Source, string Location, string? Version);

public sealed class WeChatLocateInput
{
    public string? PluginPath { get; init; }
    public string? WeChatDir { get; init; }
    public string AppData { get; init; } = "";
    public string ProgramFiles { get; init; } = "";
    public string ProgramFilesX86 { get; init; } = "";
    public IReadOnlyList<WeChatRegistryInstall> Registry { get; init; } = [];

    public static WeChatLocateInput FromConfig(OcrRuntimeConfig config)
    {
        return new WeChatLocateInput
        {
            PluginPath = config.WeChatOcrPath,
            WeChatDir = config.WeChatDir,
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Registry = WeChatRegistryReader.Read(),
        };
    }
}

public sealed class WeChatOcrLocation
{
    public bool Found { get; init; }
    public WeChatOcrKind Kind { get; init; }
    public string PluginPath { get; init; } = "";
    public string WeChatDir { get; init; } = "";
    public string LaunchExe { get; init; } = "";
    public string MmmojoPath { get; init; } = "";
    public string Report { get; init; } = "";

    public string KindName => Kind switch
    {
        WeChatOcrKind.Wx4 => "wx4",
        WeChatOcrKind.Wx3 => "wx3",
        _ => "unknown",
    };
}

/// <summary>
/// Finds WeChatOCR.exe (3.9) or wxocr.dll (4.x) and the install directory that
/// contains mmmojo_64.dll. Paths come from config, then the usual AppData plugin
/// folders, Program Files, and (on Windows) the uninstall registry.
/// </summary>
public static class WeChatOcrLocator
{
    /// <summary>Relative to %APPDATA%. Both casings: Windows is case-insensitive, tests are not.</summary>
    public static readonly string[] PluginRoots =
    [
        "Tencent/WeChat/XPlugin/Plugins/WeChatOCR",
        "Tencent/WeChat/XPlugin/plugins/WeChatOCR",
        "Tencent/WeChat/XPlugin/Plugins/WeChatOcr",
        "Tencent/xwechat/XPlugin/plugins/WeChatOcr",
        "Tencent/xwechat/XPlugin/Plugins/WeChatOcr",
        "Tencent/xwechat/XPlugin/plugins/WeChatOCR",
        "Tencent/xwechat/XPlugin/Plugins/WeChatOCR",
        "Tencent/XWeChat/XPlugin/plugins/WeChatOcr",
        "Tencent/XWeChat/XPlugin/Plugins/WeChatOcr",
    ];

    private static readonly string[] WeixinExeNames = ["weixin.exe", "Weixin.exe", "WeChat.exe"];

    public static WeChatOcrLocation Locate(WeChatLocateInput input, IWeChatPathProbe probe)
    {
        var report = new StringBuilder();
        report.AppendLine("WeChat OCR auto-detect:");
        report.AppendLine($"  appData={Show(input.AppData)}");
        report.AppendLine($"  programFiles={Show(input.ProgramFiles)}");
        report.AppendLine($"  programFilesX86={Show(input.ProgramFilesX86)}");
        if (!string.IsNullOrWhiteSpace(input.PluginPath))
            report.AppendLine($"  configured plugin={input.PluginPath}");
        if (!string.IsNullOrWhiteSpace(input.WeChatDir))
            report.AppendLine($"  configured wechatDir={input.WeChatDir}");

        List<PluginHit> plugins = FindPlugins(input, probe, report);
        List<DirHit> dirs = FindDirs(input, probe, report);

        foreach (PluginHit plugin in plugins.OrderByDescending(p => p.WriteUtc))
        {
            report.AppendLine(
                $"  candidate plugin {KindName(plugin.Kind)} mtime={plugin.WriteUtc:yyyy-MM-ddTHH:mm:ssZ} {plugin.Path}");
        }

        foreach (DirHit dir in dirs.OrderByDescending(d => d.WriteUtc))
        {
            report.AppendLine(
                $"  candidate dir mtime={dir.WriteUtc:yyyy-MM-ddTHH:mm:ssZ} {dir.Dir} mmmojo={dir.MmmojoPath} launch={dir.LaunchExe ?? "(none)"}");
        }

        if (plugins.Count == 0)
            report.AppendLine("  no WeChatOCR.exe or wxocr.dll under the usual AppData plugin folders.");
        if (dirs.Count == 0)
            report.AppendLine("  no directory containing mmmojo_64.dll was found.");

        bool explicitPlugin = !string.IsNullOrWhiteSpace(input.PluginPath);
        bool explicitDir = !string.IsNullOrWhiteSpace(input.WeChatDir);

        if (explicitPlugin && !plugins.Any(p => PathEquals(p.Path, input.PluginPath!)))
        {
            report.AppendLine($"  NOT FOUND: configured plugin does not exist: {input.PluginPath}");
            return Miss(report);
        }

        if (explicitDir && !dirs.Any(d => PathEquals(d.Dir, input.WeChatDir!)))
        {
            string mmmojo = Path.Combine(input.WeChatDir!, "mmmojo_64.dll");
            report.AppendLine($"  NOT FOUND: configured wechatDir has no mmmojo_64.dll ({mmmojo}).");
            string x86 = Path.Combine(input.WeChatDir!, "mmmojo.dll");
            if (probe.FileExists(x86))
                report.AppendLine("  Found mmmojo.dll (32-bit) in that directory. This build only supports Windows x64 / mmmojo_64.dll.");
            return Miss(report);
        }

        IEnumerable<PluginHit> pluginOrder = plugins;
        if (explicitPlugin)
            pluginOrder = plugins.Where(p => PathEquals(p.Path, input.PluginPath!));

        // Prefer 4.x when both are installed, unless the user pinned a 3.x exe.
        PluginHit[] ordered = pluginOrder
            .OrderByDescending(p => p.Kind == WeChatOcrKind.Wx4)
            .ThenByDescending(p => p.WriteUtc)
            .ToArray();

        foreach (PluginHit plugin in ordered)
        {
            DirHit? dir = SelectDir(plugin.Kind, dirs, explicitDir ? input.WeChatDir : null);
            if (dir is null)
            {
                report.AppendLine(
                    plugin.Kind == WeChatOcrKind.Wx4
                        ? $"  skip {plugin.Path}: no wechatDir with mmmojo_64.dll and weixin.exe (parent of the version folder, or the version folder itself)."
                        : $"  skip {plugin.Path}: no wechatDir with mmmojo_64.dll.");
                continue;
            }

            DirHit chosen = dir.Value;
            // 3.x launches WeChatOCR.exe. 4.x launches weixin.exe and passes wxocr.dll via switches.
            string launch = plugin.Kind == WeChatOcrKind.Wx4 ? chosen.LaunchExe ?? "" : plugin.Path;
            report.AppendLine(
                $"  selected kind={KindName(plugin.Kind)} plugin={plugin.Path} wechatDir={chosen.Dir} launch={launch} mmmojo={chosen.MmmojoPath}");
            return new WeChatOcrLocation
            {
                Found = true,
                Kind = plugin.Kind,
                PluginPath = plugin.Path,
                WeChatDir = chosen.Dir,
                LaunchExe = launch,
                MmmojoPath = chosen.MmmojoPath,
                Report = report.ToString().TrimEnd(),
            };
        }

        report.AppendLine("  NOT FOUND: could not pair an OCR plugin with a WeChat install directory.");
        return Miss(report);
    }

    private static DirHit? SelectDir(WeChatOcrKind kind, List<DirHit> dirs, string? explicitDir)
    {
        IEnumerable<DirHit> pool = dirs;
        if (!string.IsNullOrWhiteSpace(explicitDir))
            pool = dirs.Where(d => PathEquals(d.Dir, explicitDir));

        List<DirHit> list = pool.ToList();
        if (kind == WeChatOcrKind.Wx4)
        {
            list = list.Where(d => !string.IsNullOrEmpty(d.LaunchExe)).ToList();
        }
        else if (string.IsNullOrWhiteSpace(explicitDir))
        {
            // 3.9's mmmojo lives under Tencent\WeChat, not Tencent\Weixin. Don't pair
            // WeChatOCR.exe with a 4.x mmmojo just because that file is newer.
            List<DirHit> classic = list.Where(d => LooksLikeWx3Install(d.Dir)).ToList();
            if (classic.Count > 0)
                list = classic;
        }

        if (list.Count == 0)
            return null;
        return list.OrderByDescending(d => d.WriteUtc).First();
    }

    internal static bool LooksLikeWx3Install(string dir)
    {
        string norm = dir.Replace('/', '\\');
        bool wechat = norm.Contains("\\WeChat\\", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith("\\WeChat", StringComparison.OrdinalIgnoreCase);
        bool weixin = norm.Contains("\\Weixin\\", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("\\xwechat\\", StringComparison.OrdinalIgnoreCase)
            || norm.EndsWith("\\Weixin", StringComparison.OrdinalIgnoreCase);
        return wechat && !weixin;
    }

    private static List<PluginHit> FindPlugins(WeChatLocateInput input, IWeChatPathProbe probe, StringBuilder report)
    {
        var found = new List<PluginHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (!probe.FileExists(path))
                return;
            WeChatOcrKind kind = KindFromPlugin(path);
            if (kind == WeChatOcrKind.Unknown)
                return;
            if (!seen.Add(Norm(path)))
                return;
            found.Add(new PluginHit(path, kind, probe.LastWriteTimeUtc(path)));
        }

        if (!string.IsNullOrWhiteSpace(input.PluginPath))
            Add(input.PluginPath.Trim());

        if (!string.IsNullOrWhiteSpace(input.AppData))
        {
            foreach (string relative in PluginRoots)
            {
                string root = Combine(input.AppData, relative);
                report.AppendLine($"  search plugin root {root}");
                foreach (string file in probe.EnumerateFiles(root, "WeChatOCR.exe", maxDepth: 4))
                    Add(file);
                foreach (string file in probe.EnumerateFiles(root, "wxocr.dll", maxDepth: 4))
                    Add(file);
            }
        }

        return found;
    }

    private static List<DirHit> FindDirs(WeChatLocateInput input, IWeChatPathProbe probe, StringBuilder report)
    {
        var found = new List<DirHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        void AddRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (seen.Add("root:" + Norm(trimmed)))
                roots.Add(trimmed);
        }

        if (!string.IsNullOrWhiteSpace(input.WeChatDir))
            AddRoot(input.WeChatDir);

        AddRoot(Combine(input.ProgramFiles, "Tencent/Weixin"));
        AddRoot(Combine(input.ProgramFiles, "Tencent/WeChat"));
        AddRoot(Combine(input.ProgramFilesX86, "Tencent/Weixin"));
        AddRoot(Combine(input.ProgramFilesX86, "Tencent/WeChat"));

        foreach (WeChatRegistryInstall reg in input.Registry)
        {
            report.AppendLine($"  registry {reg.Source} location={reg.Location} version={reg.Version ?? "(none)"}");
            AddRoot(reg.Location);
            if (!string.IsNullOrWhiteSpace(reg.Version))
            {
                string version = reg.Version.Trim().Trim('"');
                AddRoot(Path.Combine(reg.Location, version));
                AddRoot(Path.Combine(reg.Location, "[" + version + "]"));
            }
        }

        foreach (string root in roots)
        {
            report.AppendLine($"  search mmmojo_64.dll under {root}");
            foreach (string file in probe.EnumerateFiles(root, "mmmojo_64.dll", maxDepth: 2))
            {
                string? dir = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir) || !seen.Add("dir:" + Norm(dir)))
                    continue;
                string? launch = FindWeixinExe(dir, probe);
                found.Add(new DirHit(dir, file, launch, probe.LastWriteTimeUtc(file)));
            }
        }

        return found;
    }

    internal static string? FindWeixinExe(string wechatDir, IWeChatPathProbe probe)
    {
        string? parent = Path.GetDirectoryName(wechatDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (string name in WeixinExeNames)
        {
            if (!string.IsNullOrEmpty(parent))
            {
                string inParent = Path.Combine(parent, name);
                if (probe.FileExists(inParent))
                    return inParent;
            }

            string inDir = Path.Combine(wechatDir, name);
            if (probe.FileExists(inDir))
                return inDir;
        }

        return null;
    }

    public static WeChatOcrKind KindFromPlugin(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return WeChatOcrKind.Wx4;
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return WeChatOcrKind.Wx3;
        return WeChatOcrKind.Unknown;
    }

    private static WeChatOcrLocation Miss(StringBuilder report) => new()
    {
        Found = false,
        Report = report.ToString().TrimEnd(),
    };

    private static string KindName(WeChatOcrKind kind) => kind switch
    {
        WeChatOcrKind.Wx4 => "wx4",
        WeChatOcrKind.Wx3 => "wx3",
        _ => "unknown",
    };

    private static string Combine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "";
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string path = root;
        foreach (string part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    private static string Show(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : value;

    private static string Norm(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private static bool PathEquals(string a, string b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private readonly record struct PluginHit(string Path, WeChatOcrKind Kind, DateTime WriteUtc);

    private readonly record struct DirHit(string Dir, string MmmojoPath, string? LaunchExe, DateTime WriteUtc);
}
