using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MiniOcr.Models;
using SkiaSharp;

namespace MiniOcr.Services;

/// <summary>
/// OpenAI-compatible multimodal Chat Completions for per-page vision OCR.
/// POST {baseUrl}/v1/chat/completions with image_url data URLs.
/// AOT-safe: HttpClient + source-generated JSON. Never logs apiKey.
/// </summary>
public sealed partial class LlmVisionOcr
{
    private const string SystemPrompt =
        "You are a document OCR and entity extraction engine for Chinese and English PDFs. " +
        "Given one page image, return ONLY strict JSON (no markdown fences, no commentary) with shape:\n" +
        "{\"text\":\"full page plain text\",\"ruleList\":[" +
        "{\"ruleCode\":\"B04\",\"ruleName\":\"人员名称\",\"ruleItemList\":[" +
        "{\"personName\":\"...\",\"count\":1,\"originText\":[\"10-100 char snippet containing the name\"]}]}," +
        "{\"ruleCode\":\"B06\",\"ruleName\":\"公司名称\",\"ruleItemList\":[" +
        "{\"companyName\":\"...\",\"count\":1,\"originText\":[\"10-100 char snippet containing the company\"]}]}" +
        "]}\n" +
        "Rules:\n" +
        "- text: complete readable page text (preserve reading order; omit pure noise).\n" +
        "- B04 items use personName only; B06 items use companyName only.\n" +
        "- count = number of occurrences on this page; originText length 10–100 chars each.\n" +
        "- Do not invent names absent from the image. Empty ruleList is allowed if none found.\n" +
        "- Omit a rule entirely when its ruleItemList would be empty.";

    private readonly HttpClient _http;
    private readonly LlmRuntimeConfig _config;
    private readonly ILogger<LlmVisionOcr> _logger;

    public LlmVisionOcr(
        HttpClient http,
        LlmRuntimeConfig config,
        ILogger<LlmVisionOcr> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
        if (!string.IsNullOrEmpty(config.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public LlmRuntimeConfig Config => _config;
    public bool IsUsable => _config.IsUsable;
    public int OcrConcurrency => Math.Clamp(_config.OcrConcurrency, 1, 256);

    /// <summary>Encode SKBitmap as JPEG bytes (quality 85). Caller disposes bitmap.</summary>
    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality = 85)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 40, 95));
        return data.ToArray();
    }

    public async Task<OcrPageResult> RecognizePageAsync(
        int pageNumber,
        int width,
        int height,
        byte[] jpegBytes,
        double rasterMs,
        CancellationToken ct)
    {
        if (!IsUsable)
            throw new InvalidOperationException("LLM vision OCR is not configured (need enabled + apiKey).");

        Stopwatch sw = Stopwatch.StartNew();
        string dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);
        LlmVisionOcrPayload payload = await CompleteVisionAsync(pageNumber, dataUrl, ct)
            .ConfigureAwait(false);
        sw.Stop();

        string text = (payload.Text ?? "").Replace("\r", "").Trim();
        if (text.Length > _config.OcrMaxCharsHint)
            text = text[.._config.OcrMaxCharsHint];

        List<ChallengeRule>? rules = NormalizeRuleList(payload, text);

        return new OcrPageResult
        {
            Page = pageNumber,
            Width = width,
            Height = height,
            Text = text,
            RasterizeMs = Math.Round(rasterMs, 1),
            OcrMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            RuleList = rules is { Count: > 0 } ? rules : null,
        };
    }

    private async Task<LlmVisionOcrPayload> CompleteVisionAsync(
        int pageNumber,
        string imageDataUrl,
        CancellationToken ct)
    {
        string url = _config.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
        string userText =
            $"OCR this PDF page (page {pageNumber}). " +
            $"Return full page text (aim ≤{_config.OcrMaxCharsHint} chars) and B04/B06 ruleList JSON as specified.";

        VisionChatCompletionRequest body = new()
        {
            Model = _config.Model,
            Temperature = 0,
            Messages =
            [
                new VisionChatMessage
                {
                    Role = "system",
                    Content = [new VisionContentPart { Type = "text", Text = SystemPrompt }],
                },
                new VisionChatMessage
                {
                    Role = "user",
                    Content =
                    [
                        new VisionContentPart { Type = "text", Text = userText },
                        new VisionContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new VisionImageUrl { Url = imageDataUrl },
                        },
                    ],
                },
            ],
        };

        _logger.LogInformation(
            "LLM vision OCR request: page={Page}, model={Model}, url={Url}",
            pageNumber,
            _config.Model,
            url);

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(url, body, AppJsonContext.Default.VisionChatCompletionRequest, ct)
            .ConfigureAwait(false);

        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
            throw new HttpRequestException(
                $"LLM vision OCR failed HTTP {(int)response.StatusCode}: {snippet}");
        }

        ChatCompletionResponse? parsed =
            JsonSerializer.Deserialize(raw, AppJsonContext.Default.ChatCompletionResponse);
        string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("LLM vision OCR returned empty message content.");

        string json = StripMarkdownFence(content.Trim());
        LlmVisionOcrPayload? payload =
            JsonSerializer.Deserialize(json, AppJsonContext.Default.LlmVisionOcrPayload);
        return payload ?? new LlmVisionOcrPayload();
    }

    /// <summary>
    /// Prefer model ruleList; else synthesize B04/B06 from persons/companies + page text.
    /// </summary>
    internal static List<ChallengeRule>? NormalizeRuleList(LlmVisionOcrPayload payload, string pageText)
    {
        if (payload.RuleList is { Count: > 0 })
        {
            List<ChallengeRule> cleaned = [];
            foreach (ChallengeRule rule in payload.RuleList)
            {
                if (rule.RuleItemList is null || rule.RuleItemList.Count == 0)
                    continue;
                string code = (rule.RuleCode ?? "").Trim().ToUpperInvariant();
                if (code is not ("B04" or "B06"))
                    continue;

                List<ChallengeRuleItem> items = [];
                foreach (ChallengeRuleItem item in rule.RuleItemList)
                {
                    if (code == "B04")
                    {
                        string name = NormalizeName(item.PersonName);
                        if (name.Length == 0)
                            continue;
                        List<string> origins = SanitizeOrigins(item.OriginText, pageText, name);
                        int count = item.Count > 0 ? item.Count : Math.Max(1, origins.Count);
                        items.Add(new ChallengeRuleItem
                        {
                            PersonName = name,
                            Count = count,
                            OriginText = origins,
                        });
                    }
                    else
                    {
                        string name = NormalizeName(item.CompanyName);
                        if (name.Length == 0)
                            continue;
                        List<string> origins = SanitizeOrigins(item.OriginText, pageText, name);
                        int count = item.Count > 0 ? item.Count : Math.Max(1, origins.Count);
                        items.Add(new ChallengeRuleItem
                        {
                            CompanyName = name,
                            Count = count,
                            OriginText = origins,
                        });
                    }
                }

                if (items.Count == 0)
                    continue;

                cleaned.Add(new ChallengeRule
                {
                    RuleCode = code,
                    RuleName = code == "B04" ? "人员名称" : "公司名称",
                    RuleItemList = items,
                });
            }

            return cleaned.Count > 0 ? cleaned : null;
        }

        // Fallback: companies/persons arrays → build via ChallengeResultMapper helpers
        List<string> persons = [];
        List<string> companies = [];
        if (payload.Persons is not null)
        {
            foreach (string p in payload.Persons)
            {
                string n = NormalizeName(p);
                if (n.Length > 0)
                    persons.Add(n);
            }
        }

        if (payload.Companies is not null)
        {
            foreach (string c in payload.Companies)
            {
                string n = NormalizeName(c);
                if (n.Length > 0)
                    companies.Add(n);
            }
        }

        if (persons.Count == 0 && companies.Count == 0)
            return null;

        ChallengeFileResult mapped = ChallengeResultMapper.BuildFileResult(
            "tmp",
            [new OcrPageResult { Page = 1, Text = pageText }],
            companies,
            persons);
        return mapped.Pages.Count > 0 && mapped.Pages[0].RuleList.Count > 0
            ? mapped.Pages[0].RuleList
            : null;
    }

    private static List<string> SanitizeOrigins(List<string>? raw, string pageText, string name)
    {
        List<string> result = [];
        if (raw is not null)
        {
            foreach (string o in raw)
            {
                if (string.IsNullOrWhiteSpace(o))
                    continue;
                string s = o.Trim();
                if (s.Length > ChallengeResultMapper.OriginMaxLen)
                    s = s[..ChallengeResultMapper.OriginMaxLen];
                if (s.Length >= ChallengeResultMapper.OriginMinLen)
                    result.Add(s);
                else if (s.Length > 0 && pageText.Length >= ChallengeResultMapper.OriginMinLen)
                {
                    // too short — try rebuild from page text
                    List<string> rebuilt = ChallengeResultMapper.BuildOriginTexts(pageText, name);
                    if (rebuilt.Count > 0)
                        return rebuilt;
                    result.Add(s);
                }
                else if (s.Length > 0)
                {
                    result.Add(s);
                }
            }
        }

        if (result.Count == 0 && !string.IsNullOrEmpty(pageText) && !string.IsNullOrEmpty(name))
            return ChallengeResultMapper.BuildOriginTexts(pageText, name);

        return result;
    }

    private static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        return MultiSpaceRegex().Replace(raw.Trim(), " ");
    }

    private static string StripMarkdownFence(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal))
            return content;
        int firstNl = content.IndexOf('\n');
        if (firstNl < 0)
            return content;
        int end = content.LastIndexOf("```", StringComparison.Ordinal);
        if (end <= firstNl)
            return content;
        return content[(firstNl + 1)..end].Trim();
    }

    [GeneratedRegex(@"[ \t\u3000]+")]
    private static partial Regex MultiSpaceRegex();
}
