namespace MiniOcr.Models;

/// <summary>Root of %APPDATA%/MiniOcr/config.json (camelCase).</summary>
public sealed class AppConfigFile
{
    public LlmFileConfig? Llm { get; set; }
    public OcrFileConfig? Ocr { get; set; }
}

public sealed class LlmFileConfig
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://api.openai.com";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxCharsPerRequest { get; set; } = 12000;
    /// <summary>Max parallel LLM batch completions (clamped 1–32).</summary>
    public int MaxConcurrency { get; set; } = 4;
    public bool FallbackToHeuristics { get; set; } = true;
}

public sealed class OcrFileConfig
{
    public int? Dpi { get; set; }
    public int? Engines { get; set; }
    public int? LineWorkers { get; set; }
    public int? DetThreads { get; set; }
    public int? RasterWorkers { get; set; }
    public bool? UseCls { get; set; }
    public bool AutoScaleFromCpu { get; set; } = true;
}

/// <summary>Resolved LLM settings after file + env overrides (apiKey never logged).</summary>
public sealed class LlmRuntimeConfig
{
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "https://api.openai.com";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxCharsPerRequest { get; init; } = 12000;
    public int MaxConcurrency { get; init; } = 4;
    public bool FallbackToHeuristics { get; init; } = true;

    public bool IsUsable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Model);
}

/// <summary>OpenAI chat completions request (AOT source-gen).</summary>
public sealed class ChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
    public double Temperature { get; set; }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class ChatCompletionResponse
{
    public List<ChatChoice>? Choices { get; set; }
}

public sealed class ChatChoice
{
    public ChatMessage? Message { get; set; }
}

/// <summary>Strict LLM NER payload: {"companies":["..."],"persons":["..."]}.</summary>
public sealed class LlmEntityPayload
{
    public List<string>? Companies { get; set; }
    public List<string>? Persons { get; set; }
}
