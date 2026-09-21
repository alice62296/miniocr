using MiniOcr;
using System.Text.Json;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Loads / creates MiniOcr config under ApplicationData:
/// Windows: %APPDATA%\MiniOcr\config.json
/// Linux/mac: typically ~/.config/MiniOcr/config.json
/// </summary>
public static class AppConfigStore
{
    public const string DirName = "MiniOcr";
    public const string FileName = "config.json";

    public static string GetConfigDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            DirName);

    public static string GetConfigPath() => Path.Combine(GetConfigDirectory(), FileName);

    /// <summary>
    /// Ensures directory + sample config exist, then deserializes.
    /// Never logs secrets.
    /// </summary>
    public static AppConfigFile LoadOrCreate(ILogger? logger = null)
    {
        string dir = GetConfigDirectory();
        string path = GetConfigPath();

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            logger?.LogInformation("Created config directory: {Dir}", dir);
        }

        if (!File.Exists(path))
        {
            string sample = BuildSampleJson();
            File.WriteAllText(path, sample);
            logger?.LogInformation("Wrote sample config: {Path}", path);
        }

        try
        {
            string json = File.ReadAllText(path);
            AppConfigFile? file = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfigFile);
            return file ?? new AppConfigFile();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse config at {Path}; using defaults", path);
            return new AppConfigFile();
        }
    }

    public static LlmRuntimeConfig ResolveLlm(AppConfigFile file)
    {
        LlmFileConfig llm = file.Llm ?? new LlmFileConfig();

        string baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_LLM_BASE_URL"),
            llm.BaseUrl) ?? "https://api.openai.com";

        string apiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_LLM_API_KEY"),
            llm.ApiKey) ?? "";

        string model = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MINIOCR_LLM_MODEL"),
            llm.Model) ?? "gpt-4o-mini";

        return new LlmRuntimeConfig
        {
            Enabled = llm.Enabled,
            BaseUrl = baseUrl.TrimEnd('/'),
            ApiKey = apiKey,
            Model = model,
            TimeoutSeconds = Math.Clamp(llm.TimeoutSeconds <= 0 ? 120 : llm.TimeoutSeconds, 5, 600),
            MaxCharsPerRequest = Math.Clamp(
                llm.MaxCharsPerRequest <= 0 ? 12000 : llm.MaxCharsPerRequest, 1000, 200_000),
            FallbackToHeuristics = llm.FallbackToHeuristics,
        };
    }

    private static string? FirstNonEmpty(string? a, string? b)
    {
        if (!string.IsNullOrWhiteSpace(a))
            return a.Trim();
        if (!string.IsNullOrWhiteSpace(b))
            return b.Trim();
        return null;
    }

    private static string BuildSampleJson() =>
        """
        {
          "llm": {
            "enabled": true,
            "baseUrl": "https://api.openai.com",
            "apiKey": "",
            "model": "gpt-4o-mini",
            "timeoutSeconds": 120,
            "maxCharsPerRequest": 12000,
            "fallbackToHeuristics": true
          },
          "ocr": {
            "dpi": 96,
            "engines": null,
            "lineWorkers": null,
            "detThreads": null,
            "rasterWorkers": null,
            "useCls": false,
            "autoScaleFromCpu": true
          }
        }
        """;
}
