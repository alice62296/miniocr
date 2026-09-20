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
internal partial class AppJsonContext : JsonSerializerContext;
