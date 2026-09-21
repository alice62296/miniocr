using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// OpenAI-compatible Chat Completions NER for companies/persons.
/// POST {baseUrl}/v1/chat/completions — works with OpenAI, DeepSeek, Azure-compatible, local.
/// AOT-safe: HttpClient + source-generated JSON. Never logs apiKey.
/// </summary>
public sealed partial class LlmEntityExtractor
{
    private const string SystemPrompt =
        "You extract company/organization names and person names from OCR text. " +
        "Support Chinese and English. Return ONLY strict JSON with shape " +
        "{\"companies\":[\"...\"],\"persons\":[\"...\"]}. " +
        "Do not invent names that are not present in the text. " +
        "No markdown fences, no commentary, JSON only.";

    private readonly HttpClient _http;
    private readonly LlmRuntimeConfig _config;
    private readonly ILogger<LlmEntityExtractor> _logger;

    public LlmEntityExtractor(
        HttpClient http,
        LlmRuntimeConfig config,
        ILogger<LlmEntityExtractor> logger)
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

    public async Task<OcrEntities> ExtractAsync(
        IReadOnlyList<OcrPageResult> pages,
        CancellationToken ct)
    {
        if (!IsUsable)
            throw new InvalidOperationException("LLM entity extraction is not configured.");

        List<PageBatch> batches = BuildBatches(pages, _config.MaxCharsPerRequest);
        List<string> companies = [];
        List<string> persons = [];
        object mergeLock = new();

        int concurrency = Math.Clamp(_config.MaxConcurrency, 1, 32);
        _logger.LogInformation(
            "LLM NER batches: count={BatchCount}, maxConcurrency={MaxConcurrency}",
            batches.Count,
            concurrency);

        if (batches.Count == 0)
            return MergeToEntities(pages, companies, persons);

        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = ct,
            },
            async (batch, token) =>
            {
                LlmEntityPayload payload = await CompleteBatchAsync(batch.Text, token)
                    .ConfigureAwait(false);

                List<string> localCompanies = [];
                List<string> localPersons = [];
                if (payload.Companies is not null)
                {
                    foreach (string c in payload.Companies)
                    {
                        string n = NormalizeName(c);
                        if (n.Length > 0)
                            localCompanies.Add(n);
                    }
                }

                if (payload.Persons is not null)
                {
                    foreach (string person in payload.Persons)
                    {
                        string n = NormalizeName(person);
                        if (n.Length > 0)
                            localPersons.Add(n);
                    }
                }

                if (localCompanies.Count == 0 && localPersons.Count == 0)
                    return;

                lock (mergeLock)
                {
                    companies.AddRange(localCompanies);
                    persons.AddRange(localPersons);
                }
            }).ConfigureAwait(false);

        return MergeToEntities(pages, companies, persons);
    }

    private async Task<LlmEntityPayload> CompleteBatchAsync(string userText, CancellationToken ct)
    {
        string url = _config.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
        ChatCompletionRequest body = new()
        {
            Model = _config.Model,
            Temperature = 0,
            Thinking = _config.ToThinkingOption(),
            Messages =
            [
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = userText },
            ],
        };

        _logger.LogInformation(
            "LLM NER request: model={Model}, chars={Chars}, thinking={Thinking}, url={Url}",
            _config.Model,
            userText.Length,
            body.Thinking?.Type ?? "(null)",
            url);

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(url, body, AppJsonContext.Default.ChatCompletionRequest, ct)
            .ConfigureAwait(false);

        string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string snippet = raw.Length > 240 ? raw[..240] + "…" : raw;
            throw new HttpRequestException(
                $"LLM chat completions failed HTTP {(int)response.StatusCode}: {snippet}");
        }

        ChatCompletionResponse? parsed =
            JsonSerializer.Deserialize(raw, AppJsonContext.Default.ChatCompletionResponse);
        string? content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("LLM returned empty message content.");

        string json = StripMarkdownFence(content.Trim());
        LlmEntityPayload? payload =
            JsonSerializer.Deserialize(json, AppJsonContext.Default.LlmEntityPayload);
        return payload ?? new LlmEntityPayload();
    }

    internal static List<PageBatch> BuildBatches(IReadOnlyList<OcrPageResult> pages, int maxChars)
    {
        List<PageBatch> batches = [];
        StringBuilder sb = new();
        List<int> pageNums = [];

        void Flush()
        {
            if (sb.Length == 0)
                return;
            batches.Add(new PageBatch(sb.ToString(), pageNums.ToArray()));
            sb.Clear();
            pageNums.Clear();
        }

        foreach (OcrPageResult page in pages)
        {
            string text = page.Text ?? "";
            string chunk = $"--- page {page.Page} ---\n{text}\n";
            if (sb.Length > 0 && sb.Length + chunk.Length > maxChars)
                Flush();

            // Single page larger than budget: still send alone (provider may truncate).
            if (chunk.Length > maxChars && sb.Length == 0)
            {
                sb.Append(chunk.AsSpan(0, maxChars));
                pageNums.Add(page.Page);
                Flush();
                continue;
            }

            sb.Append(chunk);
            pageNums.Add(page.Page);
        }

        Flush();
        return batches;
    }

    internal static OcrEntities MergeToEntities(
        IReadOnlyList<OcrPageResult> pages,
        IEnumerable<string> companies,
        IEnumerable<string> persons)
    {
        EntityAccumulator acc = new();
        foreach (string name in DedupPreserveOrder(companies))
        {
            for (int i = 0; i < pages.Count; i++)
            {
                string text = pages[i].Text ?? "";
                if (text.Length == 0)
                    continue;
                int count = CountOccurrences(text, name);
                for (int c = 0; c < count; c++)
                    acc.AddCompany(name, pages[i].Page);
            }
        }

        foreach (string name in DedupPreserveOrder(persons))
        {
            for (int i = 0; i < pages.Count; i++)
            {
                string text = pages[i].Text ?? "";
                if (text.Length == 0)
                    continue;
                int count = CountOccurrences(text, name);
                for (int c = 0; c < count; c++)
                    acc.AddPerson(name, pages[i].Page);
            }

            // Name returned by LLM but OCR text mismatch (OCR noise) — still keep once on first page that fuzzy-contains, else page 1 of doc.
            if (!acc.Persons.ContainsKey(name))
            {
                int page = pages.Count > 0 ? pages[0].Page : 1;
                foreach (OcrPageResult p in pages)
                {
                    if ((p.Text ?? "").Contains(name, StringComparison.Ordinal))
                    {
                        page = p.Page;
                        break;
                    }
                }

                acc.AddPerson(name, page);
            }
        }

        // Same for companies missing from page scan
        foreach (string name in DedupPreserveOrder(companies))
        {
            if (!acc.Companies.ContainsKey(name))
            {
                int page = pages.Count > 0 ? pages[0].Page : 1;
                foreach (OcrPageResult p in pages)
                {
                    if ((p.Text ?? "").Contains(name, StringComparison.Ordinal))
                    {
                        page = p.Page;
                        break;
                    }
                }

                acc.AddCompany(name, page);
            }
        }

        return EntityExtractor.ToEntities(acc);
    }

    private static IEnumerable<string> DedupPreserveOrder(IEnumerable<string> names)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string n in names)
        {
            if (seen.Add(n))
                yield return n;
        }
    }

    private static int CountOccurrences(string text, string name)
    {
        if (name.Length == 0 || text.Length < name.Length)
            return 0;
        int count = 0;
        int idx = 0;
        while (idx <= text.Length - name.Length)
        {
            int found = text.IndexOf(name, idx, StringComparison.Ordinal);
            if (found < 0)
                break;
            count++;
            idx = found + name.Length;
        }

        return count;
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

    internal readonly record struct PageBatch(string Text, int[] PageNumbers);
}
