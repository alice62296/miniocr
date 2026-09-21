using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// <summary>Max OCR text chars per LLM NER batch (clamped 1000–2_000_000). Default 300000 for long-context models.</summary>
    public int MaxCharsPerRequest { get; set; } = 300000;
    /// <summary>Max parallel LLM NER batch completions (clamped 1–32). Default 8.</summary>
    public int MaxConcurrency { get; set; } = 8;
    /// <summary>Max parallel vision OCR page calls when ocr.mode=llm (clamped 1–256).</summary>
    public int OcrConcurrency { get; set; } = 32;
    /// <summary>Hint for max chars of page text the vision model should return.</summary>
    public int OcrMaxCharsHint { get; set; } = 8000;
    /// <summary>JPEG encode quality for vision OCR pages (clamped 40–95). Default 70.</summary>
    public int OcrJpegQuality { get; set; } = 70;
    /// <summary>
    /// DeepSeek-style thinking mode (bool or "enabled"/"disabled").
    /// Default false → API sends {"type":"disabled"}. deepseek-flash/v4 enables thinking by default;
    /// NER/OCR should disable for speed.
    /// </summary>
    [JsonConverter(typeof(ThinkingConfigJsonConverter))]
    public bool Thinking { get; set; } = false;

    /// <summary>When LLM is not usable: use EntityExtractor heuristics. Ignored after an LLM NER attempt (never silent heuristic fallback). Default false.</summary>
    public bool FallbackToHeuristics { get; set; } = false;
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
    public int MaxCharsPerRequest { get; init; } = 300000;
    public int MaxConcurrency { get; init; } = 8;
    public int OcrConcurrency { get; init; } = 32;
    public int OcrMaxCharsHint { get; init; } = 8000;
    /// <summary>JPEG quality for vision page images (40–95). Default 70.</summary>
    public int OcrJpegQuality { get; init; } = 70;
    /// <summary>When true, send thinking.type=enabled; when false (default), send disabled.</summary>
    public bool Thinking { get; init; } = false;
    public bool FallbackToHeuristics { get; init; } = false;

    /// <summary>Payload for DeepSeek/OpenAI-compatible thinking field.</summary>
    public ThinkingOption ToThinkingOption() =>
        new() { Type = Thinking ? "enabled" : "disabled" };

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
    /// <summary>DeepSeek thinking control: {"type":"enabled"|"disabled"}.</summary>
    public ThinkingOption? Thinking { get; set; }
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
    /// <summary>DeepSeek thinking control: {"type":"enabled"|"disabled"}.</summary>
    public ThinkingOption? Thinking { get; set; }
}

/// <summary>OpenAI/DeepSeek thinking object serialized as camelCase <c>thinking: { type }</c>.</summary>
public sealed class ThinkingOption
{
    public string Type { get; set; } = "disabled";
}

/// <summary>
/// Accepts JSON bool, 0/1 number, or string enabled/disabled/true/false/1/0 for <c>llm.thinking</c>.
/// </summary>
public sealed class ThinkingConfigJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => false,
            JsonTokenType.Number => reader.TryGetInt64(out long n) ? n != 0 : false,
            JsonTokenType.String => ParseThinkingString(reader.GetString()),
            _ => throw new JsonException($"Unexpected token for llm.thinking: {reader.TokenType}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);

    public static bool ParseThinkingString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string s = raw.Trim();
        if (s is "1" or "true" or "True" or "TRUE" or "yes" or "YES" or "on" or "ON" or "enabled" or "Enabled" or "ENABLED")
            return true;
        if (s is "0" or "false" or "False" or "FALSE" or "no" or "NO" or "off" or "OFF" or "disabled" or "Disabled" or "DISABLED")
            return false;
        return false;
    }
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
