namespace MiniOcr.Models;

/// <summary>Root of MiniOcr config.json (camelCase). Path: see AppConfigStore.</summary>
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
    /// <summary>Max parallel LLM NER batch completions (clamped 1–32).</summary>
    public int MaxConcurrency { get; set; } = 4;
    /// <summary>Max parallel vision OCR page calls when ocr.mode=llm (clamped 1–256).</summary>
    public int OcrConcurrency { get; set; } = 32;
    /// <summary>Hint for max chars of page text the vision model should return.</summary>
    public int OcrMaxCharsHint { get; set; } = 8000;
    public bool FallbackToHeuristics { get; set; } = true;
}

public sealed class OcrFileConfig
{
    /// <summary><c>local</c> (Paddle) or <c>llm</c> (vision chat completions). Default local.</summary>
    public string Mode { get; set; } = "local";
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
    public int OcrConcurrency { get; init; } = 32;
    public int OcrMaxCharsHint { get; init; } = 8000;
    public bool FallbackToHeuristics { get; init; } = true;

    public bool IsUsable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(Model);
}

/// <summary>OpenAI chat completions request (AOT source-gen) — string message content (NER).</summary>
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

/// <summary>OpenAI-compatible multimodal chat completions (vision OCR).</summary>
public sealed class VisionChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<VisionChatMessage> Messages { get; set; } = [];
    public double Temperature { get; set; }
}

public sealed class VisionChatMessage
{
    public string Role { get; set; } = "";
    public List<VisionContentPart> Content { get; set; } = [];
}

public sealed class VisionContentPart
{
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
    public VisionImageUrl? ImageUrl { get; set; }
}

public sealed class VisionImageUrl
{
    public string Url { get; set; } = "";
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

/// <summary>
/// Vision OCR page payload. Prefer contest-shaped <see cref="RuleList"/>;
/// <see cref="Text"/> is full-page OCR text used for origin snippets / fallback mapping.
/// </summary>
public sealed class LlmVisionOcrPayload
{
    public string? Text { get; set; }
    public List<ChallengeRule>? RuleList { get; set; }
    public List<string>? Companies { get; set; }
    public List<string>? Persons { get; set; }
}
