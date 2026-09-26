namespace MiniOcr.Services;

/// <summary>
/// Optional install hints. Filesystem search is the primary detector; registry
/// covers custom install directories. Never called on non-Windows (the BCL
/// registry API is Windows-only).
/// </summary>
public static class WeChatRegistryReader
{
    private static readonly string[] UninstallKeys =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Weixin",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\WeChat",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Weixin",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WeChat",
    ];

    private static readonly string[] ProductKeys =
    [
        @"HKEY_CURRENT_USER\Software\Tencent\Weixin",
        @"HKEY_CURRENT_USER\Software\Tencent\WeChat",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Tencent\Weixin",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Tencent\WeChat",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Tencent\Weixin",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Tencent\WeChat",
    ];

    public static IReadOnlyList<WeChatRegistryInstall> Read()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var list = new List<WeChatRegistryInstall>();
        try
        {
            foreach (string key in UninstallKeys)
                AddUninstall(list, key);
            foreach (string key in ProductKeys)
                AddProduct(list, key);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return list;
        }

        return list;
    }

    private static void AddUninstall(List<WeChatRegistryInstall> list, string key)
    {
        string? location = Reg(key, "InstallLocation");
        string? version = Reg(key, "DisplayVersion");
        Add(list, key, location, version);
        AddExeDir(list, key, Reg(key, "DisplayIcon"));
    }

    private static void AddProduct(List<WeChatRegistryInstall> list, string key)
    {
        Add(list, key, Reg(key, "InstallPath") ?? Reg(key, "InstallLocation"), Reg(key, "Version") ?? Reg(key, "DisplayVersion"));
        AddExeDir(list, key, Reg(key, "InstallPath"));
    }

    private static void AddExeDir(List<WeChatRegistryInstall> list, string key, string? maybeExe)
    {
        if (string.IsNullOrWhiteSpace(maybeExe))
            return;
        string trimmed = maybeExe.Trim().Trim('"');
        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return;
        string? dir = Path.GetDirectoryName(trimmed);
        Add(list, key + " (exe dir)", dir, null);
    }

    private static void Add(List<WeChatRegistryInstall> list, string source, string? location, string? version)
    {
        if (string.IsNullOrWhiteSpace(location))
            return;
        string trimmed = location.Trim().Trim('"');
        if (trimmed.Length == 0)
            return;
        list.Add(new WeChatRegistryInstall(source, trimmed, string.IsNullOrWhiteSpace(version) ? null : version.Trim().Trim('"')));
    }

    private static string? Reg(string key, string name)
    {
        object? value = Microsoft.Win32.Registry.GetValue(key, name, null);
        return value as string;
    }
}
