using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Maps OCR page texts + entity names into competition per-page ruleList,
/// with count = occurrences on that page and originText snippets (10–100 chars).
/// </summary>
public static class ChallengeResultMapper
{
    public const int OriginMinLen = 10;
    public const int OriginMaxLen = 100;

    public static ChallengeFileResult BuildFileResult(string fileId, OcrResponse ocr)
    {
        // Vision OCR may already attach contest-shaped ruleList per page.
        if (ocr.Pages.Any(p => p.RuleList is { Count: > 0 }))
        {
            List<ChallengePageResult> pages = new(ocr.Pages.Count);
            foreach (OcrPageResult page in ocr.Pages)
            {
                pages.Add(new ChallengePageResult
                {
                    Page = page.Page,
                    RuleList = page.RuleList is { Count: > 0 }
                        ? page.RuleList
                        : [],
                });
            }

            return new ChallengeFileResult { FileId = fileId, Pages = pages };
        }

        OcrEntities entities = ocr.Entities ?? new OcrEntities();
        List<string> companies = entities.Companies.Select(c => c.Name).Where(n => n.Length > 0).ToList();
        List<string> persons = entities.Persons.Select(p => p.Name).Where(n => n.Length > 0).ToList();
        return BuildFileResult(fileId, ocr.Pages, companies, persons);
    }

    public static ChallengeFileResult BuildFileResult(
        string fileId,
        IReadOnlyList<OcrPageResult> pages,
        IReadOnlyList<string> companyNames,
        IReadOnlyList<string> personNames)
    {
        List<ChallengePageResult> pageResults = new(pages.Count);
        foreach (OcrPageResult page in pages)
        {
            string text = page.Text ?? "";
            List<ChallengeRuleItem> personItems = BuildPersonItems(text, personNames);
            List<ChallengeRuleItem> companyItems = BuildCompanyItems(text, companyNames);

            List<ChallengeRule> rules = [];
            if (personItems.Count > 0)
            {
                rules.Add(new ChallengeRule
                {
                    RuleCode = "B04",
                    RuleName = "人员名称",
                    RuleItemList = personItems,
                });
            }

            if (companyItems.Count > 0)
            {
                rules.Add(new ChallengeRule
                {
                    RuleCode = "B06",
                    RuleName = "公司名称",
                    RuleItemList = companyItems,
                });
            }

            pageResults.Add(new ChallengePageResult
            {
                Page = page.Page,
                RuleList = rules,
            });
        }

        return new ChallengeFileResult
        {
            FileId = fileId,
            Pages = pageResults,
        };
    }

    private static List<ChallengeRuleItem> BuildPersonItems(string text, IReadOnlyList<string> names)
    {
        List<ChallengeRuleItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;
            List<string> origins = BuildOriginTexts(text, name);
            if (origins.Count == 0)
                continue;
            items.Add(new ChallengeRuleItem
            {
                PersonName = name,
                Count = origins.Count,
                OriginText = origins,
            });
        }

        return items;
    }

    private static List<ChallengeRuleItem> BuildCompanyItems(string text, IReadOnlyList<string> names)
    {
        List<ChallengeRuleItem> items = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;
            List<string> origins = BuildOriginTexts(text, name);
            if (origins.Count == 0)
                continue;
            items.Add(new ChallengeRuleItem
            {
                CompanyName = name,
                Count = origins.Count,
                OriginText = origins,
            });
        }

        return items;
    }

    /// <summary>
    /// For each occurrence of <paramref name="name"/> in <paramref name="pageText"/>,
    /// emit one excerpt of length in [10, 100] centered on the match when possible.
    /// </summary>
    public static List<string> BuildOriginTexts(string pageText, string name)
    {
        List<string> texts = [];
        if (string.IsNullOrEmpty(pageText) || string.IsNullOrEmpty(name) || pageText.Length < name.Length)
            return texts;

        int idx = 0;
        while (idx <= pageText.Length - name.Length)
        {
            int found = pageText.IndexOf(name, idx, StringComparison.Ordinal);
            if (found < 0)
                break;

            string snippet = SliceAround(pageText, found, name.Length);
            if (snippet.Length >= OriginMinLen)
                texts.Add(snippet);
            else if (pageText.Length >= OriginMinLen)
                texts.Add(PadToMin(pageText, found, name.Length));
            else
                texts.Add(pageText); // whole page shorter than 10 — still report something

            idx = found + Math.Max(1, name.Length);
        }

        return texts;
    }

    private static string SliceAround(string text, int matchStart, int matchLen)
    {
        // Prefer ~max window centered on the name; clamp to [min, max].
        int target = OriginMaxLen;
        int extra = Math.Max(0, target - matchLen);
        int left = extra / 2;
        int right = extra - left;

        int start = Math.Max(0, matchStart - left);
        int end = Math.Min(text.Length, matchStart + matchLen + right);

        // If we hit a boundary, borrow from the other side to approach target length.
        int deficit = target - (end - start);
        if (deficit > 0)
        {
            start = Math.Max(0, start - deficit);
            deficit = target - (end - start);
            if (deficit > 0)
                end = Math.Min(text.Length, end + deficit);
        }

        string s = text[start..end];
        if (s.Length > OriginMaxLen)
            s = s[..OriginMaxLen];
        return s;
    }

    private static string PadToMin(string text, int matchStart, int matchLen)
    {
        int start = Math.Max(0, Math.Min(matchStart, text.Length - OriginMinLen));
        int end = Math.Min(text.Length, start + OriginMinLen);
        if (end - start < OriginMinLen)
            start = Math.Max(0, end - OriginMinLen);
        string s = text[start..end];
        if (s.Length > OriginMaxLen)
            s = s[..OriginMaxLen];
        return s;
    }
}
