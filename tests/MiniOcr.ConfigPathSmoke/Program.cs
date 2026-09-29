using System.Runtime.InteropServices;
using MiniOcr.Services;

int failed = 0;

void AssertTrue(bool cond, string msg)
{
    if (cond)
    {
        Console.WriteLine("  PASS  " + msg);
        return;
    }

    Console.WriteLine("  FAIL  " + msg);
    failed++;
}

void AssertEqual(string expected, string actual, string msg)
{
    AssertTrue(
        string.Equals(expected, actual, StringComparison.Ordinal),
        msg + $" (expected '{expected}', got '{actual}')");
}

Console.WriteLine("=== AppConfigStore path resolution smoke ===");

string home = "/Users/demo";
string appSupport = "/Users/demo/Library/Application Support";
string xdg = "/Users/demo/.config/MiniOcr/config.json";
string macPrimary = "/Users/demo/Library/Application Support/MiniOcr/config.json";

// macOS: empty ApplicationData → HOME-based Application Support (not relative MiniOcr/...)
{
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: "",
        homeDirectory: home,
        os: OSPlatform.OSX);
    AssertEqual(macPrimary, resolved.Path, "mac empty AppData → Application Support canonical");
    AssertEqual("canonical", resolved.Source, "mac empty AppData source=canonical");
    AssertTrue(!resolved.Path.StartsWith("MiniOcr", StringComparison.Ordinal), "mac path must not be relative MiniOcr/...");
}

// macOS: candidates include Application Support then ~/.config
{
    var candidates = AppConfigStore.EnumerateCandidatePaths(OSPlatform.OSX, appSupport, home).ToList();
    AssertTrue(candidates.Count >= 2, "mac has ≥2 candidates");
    AssertEqual(macPrimary, candidates[0], "mac candidate[0]=Application Support");
    AssertEqual(xdg, candidates[1], "mac candidate[1]=~/.config");
}

// macOS: existing ~/.config wins over missing Application Support (read-without-move)
{
    string tmpRoot = Path.Combine(Path.GetTempPath(), "miniocr-config-smoke-" + Guid.NewGuid().ToString("N"));
    string fakeHome = Path.Combine(tmpRoot, "home");
    string fakeAppSupport = Path.Combine(fakeHome, "Library", "Application Support");
    string xdgDir = Path.Combine(fakeHome, ".config", "MiniOcr");
    Directory.CreateDirectory(xdgDir);
    string xdgFile = Path.Combine(xdgDir, "config.json");
    File.WriteAllText(xdgFile, """{"llm":{"enabled":true,"apiKey":"from-xdg"}}""");

    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: fakeAppSupport,
        homeDirectory: fakeHome,
        os: OSPlatform.OSX);

    AssertEqual(Path.GetFullPath(xdgFile), resolved.Path, "mac existing ~/.config is used");
    AssertEqual("existing", resolved.Source, "mac ~/.config source=existing");
    AssertTrue(resolved.Existed, "mac ~/.config existed=true");

    // LoadOrCreate should read in place (not move to Application Support)
    string? prev = Environment.GetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar);
    try
    {
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, null);
        // Resolve via explicit env to avoid touching real AppData
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, xdgFile);
        var loaded = AppConfigStore.LoadOrCreate();
        AssertEqual(Path.GetFullPath(xdgFile), loaded.ConfigPath, "LoadOrCreate keeps xdg path");
        AssertTrue(loaded.ConfigFileExisted, "LoadOrCreate existed=true for xdg");
        AssertTrue(loaded.Config.Llm?.ApiKey == "from-xdg", "LoadOrCreate reads xdg apiKey");
        AssertTrue(!Directory.Exists(Path.Combine(fakeAppSupport, "MiniOcr")), "did not create Application Support copy");
    }
    finally
    {
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, prev);
        try { Directory.Delete(tmpRoot, recursive: true); } catch { /* ignore */ }
    }
}

// Env override wins
{
    string custom = Path.Combine(Path.GetTempPath(), "miniocr-custom-config.json");
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: custom,
        applicationData: appSupport,
        homeDirectory: home,
        os: OSPlatform.OSX);
    AssertEqual(Path.GetFullPath(custom), resolved.Path, "MINIOCR_CONFIG_PATH wins");
    AssertEqual("env", resolved.Source, "env source");
}

// Linux canonical is ~/.config
{
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: "",
        homeDirectory: "/home/demo",
        os: OSPlatform.Linux);
    AssertEqual("/home/demo/.config/MiniOcr/config.json", resolved.Path, "linux canonical ~/.config");
}

// Windows: empty AppData → USERPROFILE Roaming
{
    var resolved = AppConfigStore.ResolveConfigPath(
        envConfigPath: "",
        applicationData: "",
        homeDirectory: @"C:\Users\demo",
        os: OSPlatform.Windows);
    string expected = @"C:\Users\demo\AppData\Roaming\MiniOcr\config.json";
    AssertEqual(expected, resolved.Path, "win empty AppData → Roaming fallback");
}

// A config written while pageGroupOverlap existed must still load. The key is ignored.
{
    string tmp = Path.Combine(Path.GetTempPath(), "miniocr-legacy-overlap-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(tmp, """
    {
      "llm": {
        "enabled": true,
        "apiKey": "legacy-key",
        "pagesPerRequest": 12,
        "pageGroupOverlap": 1
      }
    }
    """);
    string? prev = Environment.GetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar);
    string? prevOverlap = Environment.GetEnvironmentVariable("MINIOCR_LLM_PAGE_GROUP_OVERLAP");
    try
    {
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, tmp);
        Environment.SetEnvironmentVariable("MINIOCR_LLM_PAGE_GROUP_OVERLAP", "3");
        var loaded = AppConfigStore.LoadOrCreate();
        AssertTrue(loaded.Config.Llm?.ApiKey == "legacy-key", "legacy pageGroupOverlap config loads");
        AssertEqual("12", (loaded.Config.Llm?.PagesPerRequest ?? 0).ToString(), "pagesPerRequest still read beside ignored overlap key");
        var llm = AppConfigStore.ResolveLlm(loaded.Config);
        AssertEqual("12", llm.PagesPerRequest.ToString(), "runtime pagesPerRequest ignores leftover overlap env");
        AssertEqual("legacy-key", llm.ApiKey, "runtime apiKey from legacy config");
    }
    finally
    {
        Environment.SetEnvironmentVariable(AppConfigStore.ConfigPathEnvVar, prev);
        Environment.SetEnvironmentVariable("MINIOCR_LLM_PAGE_GROUP_OVERLAP", prevOverlap);
        try { File.Delete(tmp); } catch { /* ignore */ }
    }
}

Console.WriteLine(failed == 0 ? "\nAll config-path checks passed." : $"\n{failed} config-path check(s) failed.");
return failed == 0 ? 0 : 1;
