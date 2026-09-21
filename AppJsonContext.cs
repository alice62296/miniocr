using System.Text.Json.Serialization;
using MiniOcr.Models;

namespace MiniOcr;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OcrUrlRequest))]
[JsonSerializable(typeof(OcrResponse))]
[JsonSerializable(typeof(OcrPageResult))]
[JsonSerializable(typeof(OcrTimings))]
[JsonSerializable(typeof(OcrEntities))]
[JsonSerializable(typeof(EntityHit))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(List<OcrPageResult>))]
[JsonSerializable(typeof(List<EntityHit>))]
[JsonSerializable(typeof(AppConfigFile))]
[JsonSerializable(typeof(LlmFileConfig))]
[JsonSerializable(typeof(OcrFileConfig))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatChoice))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(LlmEntityPayload))]
[JsonSerializable(typeof(List<ChatMessage>))]
[JsonSerializable(typeof(List<ChatChoice>))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppJsonContext : JsonSerializerContext;
