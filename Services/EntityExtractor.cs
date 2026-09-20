using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Heuristic AOT-safe NER for Chinese/English company and person names.
/// Regex + compiled surname HashSet — no ML packages. Not perfect; see README.
/// </summary>
public static partial class EntityExtractor
{
    // Longest-first org suffixes so "股份有限公司" wins over "有限公司".
    private static readonly string[] ChineseOrgSuffixes =
    [
        "股份有限公司", "有限责任公司", "有限公司", "集团公司", "集团有限公司",
        "律师事务所", "会计师事务所", "事务所",
        "商业银行", "银行", "信用社",
        "大学", "学院", "医院", "研究院", "研究所", "基金会", "委员会",
        "控股公司", "投资公司", "科技公司", "技术公司",
        "集团", "中心", "工厂",
    ];

    private static readonly string[] PersonTitles =
    [
        "法定代表人", "总经理", "董事长", "副总经理", "副董事长",
        "先生", "女士", "经理", "董事", "主任", "局长", "处长", "总监",
    ];

    private static readonly HashSet<string> SingleSurnames;
    private static readonly HashSet<string> CompoundSurnames;
    private static readonly HashSet<string> PersonReject;

    // Connectors / junk when walking back from an org suffix.
    private static readonly HashSet<char> CompanyBreakChars =
    [
        '\n', '\r', '\t', ' ', '　',
        '，', ',', '。', '.', '；', ';', '：', ':', '、', '/', '|',
        '（', '）', '(', ')', '【', '】', '[', ']', '"', '\'', '“', '”',
        '《', '》', '<', '>',
    ];

    private static readonly string[] CompanyLeftStop =
    [
        // Longest-first so "买方为" wins over "为".
        "买方为", "卖方为", "甲方为", "乙方为", "丙方为",
        "买方是", "卖方是", "甲方是", "乙方是",
        "系", "为", "是", "由", "及", "与", "和", "对", "向", "在",
        "的", "等", "或",
    ];

    static EntityExtractor()
    {
        const string singles =
            "赵钱孙李周吴郑王冯陈褚卫蒋沈韩杨朱秦尤许何吕施张孔曹严华金魏陶姜" +
            "戚谢邹喻柏水窦章云苏潘葛奚范彭郎鲁韦昌马苗凤花方俞任袁柳酆鲍史唐" +
            "费廉岑薛雷贺倪汤滕殷罗毕郝邬安常乐于时傅皮卞齐康伍余元卜顾孟平黄" +
            "和穆萧尹姚邵湛汪祁毛禹狄米贝明臧计伏成戴谈宋茅庞熊纪舒屈项祝董梁" +
            "杜阮蓝闵席季麻强贾路娄危江童颜郭梅盛林刁钟徐邱骆高夏蔡田樊胡凌霍" +
            "虞万支柯昝管卢莫经房裘缪干解应宗丁宣贲邓郁单杭洪包诸左石崔吉钮龚" +
            "程嵇邢滑裴陆荣翁荀羊於惠甄曲家封芮羿储靳汲邴糜松井段富巫乌焦巴弓" +
            "牧隗山谷车侯宓蓬全郗班仰秋仲伊宫宁仇栾暴甘钭厉戎祖武符刘景詹束龙" +
            "叶幸司韶郜黎蓟薄印宿白怀蒲邰从鄂索咸籍赖卓蔺屠蒙池乔阴鬱胥能苍双" +
            "闻莘党翟谭贡劳逄姬申扶堵冉宰郦雍郤璩桑桂濮牛寿通边扈燕冀郏浦尚农" +
            "温别庄晏柴瞿阎充慕连茹习宦艾鱼容向古易慎戈廖庾终暨居衡步都耿满弘" +
            "匡国文寇广禄阙东欧殳沃利蔚越夔隆师巩厍聂晁勾敖融冷訾辛阚那简饶空" +
            "曾毋沙乜养鞠须丰巢关蒯相查后荆红游竺权逯盖益桓公";

        SingleSurnames = new HashSet<string>(singles.Length);
        for (int i = 0; i < singles.Length; i++)
            SingleSurnames.Add(singles[i].ToString());

        CompoundSurnames = new HashSet<string>(StringComparer.Ordinal)
        {
            "欧阳", "太史", "端木", "上官", "司马", "东方", "独孤", "南宫",
            "万俟", "闻人", "夏侯", "诸葛", "尉迟", "公羊", "赫连", "澹台",
            "皇甫", "宗政", "濮阳", "公冶", "太叔", "申屠", "公孙", "慕容",
            "仲孙", "钟离", "长孙", "宇文", "司徒", "鲜于", "司空", "闾丘",
            "子车", "亓官", "司寇", "巫马", "公西", "颛孙", "壤驷", "公良",
            "漆雕", "乐正", "宰父", "谷梁", "拓跋", "夹谷", "轩辕", "令狐",
            "段干", "百里", "呼延", "东郭", "南门", "羊舌", "微生", "公户",
            "公玉", "公仪", "梁丘", "公仲", "公上", "公门", "公山", "公坚",
            "左丘", "东门", "西门", "南荣", "即墨",
        };

        PersonReject = new HashSet<string>(StringComparer.Ordinal)
        {
            "公司", "有限", "股份", "责任", "集团", "银行", "大学", "学院", "医院",
            "经理", "董事", "主任", "局长", "处长", "总监", "先生", "女士",
            "申请人", "被申请人", "原告人", "被告人", "当事人", "第三人",
            "负责人", "联系人", "经办人", "委托人", "代理人", "代表人",
            "法定代表", "本公司", "该公司", "办公室", "委员会",
            "文字", "日期", "编号", "合同", "协议", "附件", "页码",
        };
    }

    public static void ExtractPage(string? text, int pageNumber, EntityAccumulator acc)
    {
        if (string.IsNullOrWhiteSpace(text) || pageNumber <= 0)
            return;

        CollectCompanies(text, pageNumber, acc);
        CollectPersons(text, pageNumber, acc);
    }

    public static OcrEntities ToEntities(EntityAccumulator acc)
    {
        return new OcrEntities
        {
            Companies = SuppressShorterPrefixes(Finalize(acc.Companies)),
            Persons = Finalize(acc.Persons),
        };
    }

    /// <summary>
    /// Drop "上海浦东发展银行" when "上海浦东发展银行股份有限公司" is also present.
    /// </summary>
    private static List<EntityHit> SuppressShorterPrefixes(List<EntityHit> companies)
    {
        if (companies.Count <= 1)
            return companies;

        List<EntityHit> kept = new(companies.Count);
        for (int i = 0; i < companies.Count; i++)
        {
            EntityHit c = companies[i];
            bool dominated = false;
            for (int j = 0; j < companies.Count; j++)
            {
                if (i == j)
                    continue;
                EntityHit other = companies[j];
                if (other.Name.Length > c.Name.Length &&
                    other.Name.Contains(c.Name, StringComparison.Ordinal))
                {
                    dominated = true;
                    break;
                }
            }

            if (!dominated)
                kept.Add(c);
        }

        return kept;
    }

    public static OcrEntities ExtractFromPages(IReadOnlyList<string> pageTexts)
    {
        EntityAccumulator acc = new();
        for (int i = 0; i < pageTexts.Count; i++)
            ExtractPage(pageTexts[i], i + 1, acc);
        return ToEntities(acc);
    }

    private static List<EntityHit> Finalize(ConcurrentDictionary<string, PageHits> map)
    {
        List<EntityHit> list = new(map.Count);
        foreach (KeyValuePair<string, PageHits> kv in map)
        {
            PageHits ph = kv.Value;
            int[] pages;
            lock (ph)
                pages = ph.Pages.OrderBy(p => p).ToArray();

            list.Add(new EntityHit
            {
                Name = kv.Key,
                Pages = pages.ToList(),
                Count = ph.Count,
            });
        }

        list.Sort(static (a, b) =>
        {
            int c = b.Count.CompareTo(a.Count);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });
        return list;
    }

    private static void CollectCompanies(string text, int page, EntityAccumulator acc)
    {
        foreach (Match m in CompanyLabelRegex().Matches(text))
        {
            string? cleaned = CleanCompany(m.Groups[1].Value, requireOrgSignal: false);
            if (cleaned is not null)
                acc.AddCompany(cleaned, page);
        }

        CollectChineseCompaniesBySuffix(text, page, acc);

        foreach (Match m in EnglishCompanyRegex().Matches(text))
        {
            string? cleaned = CleanCompany(m.Value, requireOrgSignal: true);
            if (cleaned is not null)
                acc.AddCompany(cleaned, page);
        }
    }

    private static void CollectChineseCompaniesBySuffix(string text, int page, EntityAccumulator acc)
    {
        // Suffix-anchored lookback avoids swallowing leading prose (e.g. "买方为李宁…有限公司").
        foreach (string suffix in ChineseOrgSuffixes)
        {
            int search = 0;
            while (search < text.Length)
            {
                int idx = text.IndexOf(suffix, search, StringComparison.Ordinal);
                if (idx < 0)
                    break;

                int end = idx + suffix.Length;
                int start = FindCompanyStart(text, idx);
                if (end - start >= 2 + suffix.Length || end - start >= 4)
                {
                    string raw = text[start..end];
                    string? cleaned = CleanCompany(raw, requireOrgSignal: true);
                    if (cleaned is not null)
                        acc.AddCompany(cleaned, page);
                }

                search = idx + suffix.Length;
            }
        }
    }

    private static int FindCompanyStart(string text, int suffixIndex)
    {
        int i = suffixIndex - 1;
        int min = Math.Max(0, suffixIndex - 40);
        while (i >= min)
        {
            char c = text[i];
            if (CompanyBreakChars.Contains(c))
                return i + 1;
            if (!IsCompanyNameChar(c))
                return i + 1;
            i--;
        }

        int start = i + 1;
        // Strip leading connectors only (StartsWith), longest first — never mid-string IndexOf
        // (that turned "买方为李宁…" into "育用品…" via a second hit on "为").
        bool stripped;
        do
        {
            stripped = false;
            string head = text[start..suffixIndex];
            foreach (string stop in CompanyLeftStop)
            {
                if (head.StartsWith(stop, StringComparison.Ordinal) &&
                    start + stop.Length < suffixIndex)
                {
                    start += stop.Length;
                    stripped = true;
                    break;
                }
            }
        } while (stripped);

        return start;
    }

    private static bool IsCompanyNameChar(char c) =>
        IsCjk(c) ||
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') ||
        c is '（' or '）' or '(' or ')' or '-' or '·' or '•' or '&' or '.';

    private static void CollectPersons(string text, int page, EntityAccumulator acc)
    {
        foreach (Match m in PersonLabelRegex().Matches(text))
        {
            if (TryNormalizePerson(m.Groups[1].Value.Trim(), out string name))
                acc.AddPerson(name, page);
        }

        foreach (Match m in PersonTitleRegex().Matches(text))
        {
            if (TryNormalizePerson(m.Groups[1].Value.Trim(), out string name))
                acc.AddPerson(name, page);
        }

        ScanSurnamePersons(text, page, acc);

        foreach (Match m in EnglishPersonRegex().Matches(text))
        {
            string name = NormalizeSpaces(m.Value);
            if (name.Length is >= 3 and <= 40 && !LooksLikeEnglishOrg(name))
                acc.AddPerson(name, page);
        }
    }

    private static void ScanSurnamePersons(string text, int page, EntityAccumulator acc)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        for (int i = 0; i < span.Length; )
        {
            if (!HasPersonLeftBoundary(span, i))
            {
                i++;
                continue;
            }

            if (i + 3 <= span.Length &&
                IsCjk(span[i]) && IsCjk(span[i + 1]) &&
                CompoundSurnames.Contains(span.Slice(i, 2).ToString()))
            {
                if (TryTakePersonAt(span, text, i, surnameLen: 2, page, acc, out int consumed))
                {
                    i += consumed;
                    continue;
                }
            }

            if (i + 2 <= span.Length &&
                IsCjk(span[i]) &&
                SingleSurnames.Contains(span[i].ToString()))
            {
                if (TryTakePersonAt(span, text, i, surnameLen: 1, page, acc, out int consumed))
                {
                    i += consumed;
                    continue;
                }
            }

            i++;
        }
    }

    private static bool TryTakePersonAt(
        ReadOnlySpan<char> span,
        string text,
        int i,
        int surnameLen,
        int page,
        EntityAccumulator acc,
        out int consumed)
    {
        consumed = 1;
        // Prefer longer given name when valid (张伟强 before 张伟), but require right boundary.
        for (int given = 2; given >= 1; given--)
        {
            int len = surnameLen + given;
            if (i + len > span.Length)
                continue;

            // Given chars must be CJK / middot and not start an org suffix.
            bool givenOk = true;
            for (int g = 0; g < given; g++)
            {
                char c = span[i + surnameLen + g];
                if (!IsCjk(c) && c != '·' && c != '•')
                {
                    givenOk = false;
                    break;
                }

                if (StartsWithOrgSuffix(span, i + surnameLen + g))
                {
                    givenOk = false;
                    break;
                }
            }

            if (!givenOk)
                continue;

            if (!HasPersonRightBoundary(span, i + len))
                continue;

            string candidate = span.Slice(i, len).ToString();
            if (!TryNormalizePerson(candidate, out string name))
                continue;

            if (IsInsideLongerOrg(text, i, name.Length))
                continue;

            acc.AddPerson(name, page);
            consumed = len;
            return true;
        }

        return false;
    }

    private static bool HasPersonLeftBoundary(ReadOnlySpan<char> span, int i)
    {
        if (i == 0)
            return true;
        char prev = span[i - 1];
        // Do not start a person mid CJK run (suppresses 英文方→文方, 说明文字→明文字).
        if (IsCjk(prev))
            return false;
        return true;
    }

    private static bool HasPersonRightBoundary(ReadOnlySpan<char> span, int after)
    {
        if (after >= span.Length)
            return true;

        char next = span[after];
        if (!IsCjk(next) && next != '·' && next != '•')
            return true;

        // Allow immediate Chinese title suffixes: 张伟先生 / 陈晓东经理
        foreach (string title in PersonTitles)
        {
            if (after + title.Length <= span.Length &&
                span.Slice(after, title.Length).SequenceEqual(title.AsSpan()))
                return true;
        }

        return false;
    }

    private static bool StartsWithOrgSuffix(ReadOnlySpan<char> span, int i)
    {
        foreach (string s in ChineseOrgSuffixes)
        {
            if (i + s.Length <= span.Length && span.Slice(i, s.Length).SequenceEqual(s.AsSpan()))
                return true;
        }

        return false;
    }

    private static bool IsInsideLongerOrg(string text, int index, int nameLen)
    {
        int windowStart = Math.Max(0, index - 8);
        int windowEnd = Math.Min(text.Length, index + nameLen + 20);
        ReadOnlySpan<char> window = text.AsSpan(windowStart, windowEnd - windowStart);
        foreach (string suffix in ChineseOrgSuffixes)
        {
            int idx = window.IndexOf(suffix.AsSpan(), StringComparison.Ordinal);
            while (idx >= 0)
            {
                int abs = windowStart + idx;
                if (abs >= index && abs - (index + nameLen) <= 12)
                    return true;

                int next = idx + 1;
                if (next >= window.Length)
                    break;
                int rel = window.Slice(next).IndexOf(suffix.AsSpan(), StringComparison.Ordinal);
                idx = rel < 0 ? -1 : next + rel;
            }
        }

        return false;
    }

    private static string? CleanCompany(string raw, bool requireOrgSignal)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string s = NormalizeSpaces(raw).Trim();
        s = TrimCompanyEdges(s);
        s = CompanyLeadingJunkRegex().Replace(s, "");
        s = NormalizeSpaces(s);
        if (s.Length is < 2 or > 60)
            return null;

        bool hasChineseSuffix = false;
        foreach (string suffix in ChineseOrgSuffixes)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                hasChineseSuffix = true;
                break;
            }
        }

        bool hasEnglishSuffix = EnglishCompanySuffixRegex().IsMatch(s);
        int cjk = CountCjk(s);

        if (requireOrgSignal && !hasChineseSuffix && !hasEnglishSuffix)
            return null;

        if (!hasChineseSuffix && !hasEnglishSuffix && cjk < 2)
            return null;

        // Label captures without suffix: need a reasonably long CJK org phrase.
        if (!hasChineseSuffix && !hasEnglishSuffix && s.Length < 4)
            return null;

        // Canonicalize English suffix punctuation: "Ltd." / "Ltd"
        s = CanonicalizeEnglishCompany(s);
        return s;
    }

    private static string TrimCompanyEdges(string s)
    {
        // Keep trailing '.' when it belongs to Ltd./Inc./Corp./Co.
        int start = 0;
        int end = s.Length;
        while (start < end && IsTrimEdge(s[start], trailing: false))
            start++;
        while (end > start && IsTrimEdge(s[end - 1], trailing: true))
        {
            if (s[end - 1] == '.' && IsEnglishAbbrevDot(s, end - 1))
                break;
            end--;
        }

        return s[start..end];
    }

    private static bool IsTrimEdge(char c, bool trailing)
    {
        if (c is ' ' or '\t' or '　' or '、' or '，' or ',' or '。' or ';' or '；' or ':' or '：'
            or '"' or '\'' or '“' or '”' or '（' or '）' or '(' or ')')
            return true;
        // Strip leading dots only; trailing abbrev dots handled by caller.
        return c == '.' && !trailing;
    }

    private static bool IsEnglishAbbrevDot(string s, int dotIndex)
    {
        // …Ltd. / Inc. / Corp. / Co.
        ReadOnlySpan<char> before = s.AsSpan(0, dotIndex);
        return before.EndsWith("Ltd", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("Inc", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("Corp", StringComparison.OrdinalIgnoreCase)
            || before.EndsWith("Co", StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalizeEnglishCompany(string s)
    {
        s = EnsureAbbrevDot(s, "Ltd");
        s = EnsureAbbrevDot(s, "Inc");
        s = EnsureAbbrevDot(s, "Corp");
        return s;
    }

    private static string EnsureAbbrevDot(string s, string abbrev)
    {
        if (s.EndsWith(abbrev, StringComparison.OrdinalIgnoreCase) &&
            !s.EndsWith(abbrev + ".", StringComparison.OrdinalIgnoreCase))
            return s + ".";
        return s;
    }

    private static bool TryNormalizePerson(string raw, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string s = NormalizeSpaces(raw).Trim(' ', '\t', '　', '、', '，', ',', '。', '.');
        s = s.Replace("•", "·", StringComparison.Ordinal);
        if (s.Length is < 2 or > 4)
            return false;

        if (PersonReject.Contains(s))
            return false;

        foreach (string stop in PersonReject)
        {
            if (stop.Length >= 2 && s.Contains(stop, StringComparison.Ordinal))
                return false;
        }

        foreach (string suffix in ChineseOrgSuffixes)
        {
            if (s.Contains(suffix, StringComparison.Ordinal))
                return false;
        }

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!IsCjk(c) && c != '·')
                return false;
        }

        bool okSurname =
            (s.Length >= 3 && CompoundSurnames.Contains(s[..2])) ||
            (s.Length >= 2 && SingleSurnames.Contains(s[0].ToString()));
        if (!okSurname)
            return false;

        // Reject title-only leftovers.
        foreach (string title in PersonTitles)
        {
            if (s == title || s.EndsWith(title, StringComparison.Ordinal))
                return false;
        }

        name = s;
        return true;
    }

    private static bool LooksLikeEnglishOrg(string name) =>
        EnglishCompanySuffixRegex().IsMatch(name) ||
        name.Contains("University", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Company", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSpaces(string s) =>
        MultiSpaceRegex().Replace(s.Trim(), " ");

    private static int CountCjk(string s)
    {
        int n = 0;
        foreach (char c in s)
        {
            if (IsCjk(c))
                n++;
        }

        return n;
    }

    private static bool IsCjk(char c) =>
        c is (>= '\u4e00' and <= '\u9fff') or (>= '\u3400' and <= '\u4dbf');

    [GeneratedRegex(
        @"(?:公司名称|企业名称|单位名称|甲方|乙方|丙方|委托方|受托方|出卖人|买受人)[：:\s]+([^\n\r，。；;]{2,40})",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompanyLabelRegex();

    [GeneratedRegex(
        @"\b[A-Z][A-Za-z0-9&.\-]*(?:[ \t]+[A-Z][A-Za-z0-9&.\-]*)*(?:[ \t]*,?[ \t]*)?(?:Inc\.?|Ltd\.?|LLC|Corp\.?|Co\.|Company|Corporation)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishCompanyRegex();

    [GeneratedRegex(
        @"(?:Inc\.?|Ltd\.?|LLC|Corp\.?|Co\.|Company|Corporation)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EnglishCompanySuffixRegex();

    [GeneratedRegex(
        @"(?:姓名|负责人|法定代表人|联系人|经办人|委托人|代理人|签字|签署人)[：:\s]+([\u4e00-\u9fff·•]{2,4})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PersonLabelRegex();

    [GeneratedRegex(
        @"([\u4e00-\u9fff]{2,4})(?:先生|女士|经理|董事|总经理|董事长|主任|局长|处长|总监)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PersonTitleRegex();

    [GeneratedRegex(
        @"\b[A-Z][a-z]+(?:[ \t]+[A-Z][a-z]+)+\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnglishPersonRegex();

    [GeneratedRegex(@"^[的与和及及\/\-|]+")]
    private static partial Regex CompanyLeadingJunkRegex();

    [GeneratedRegex(@"[ \t\u3000]+")]
    private static partial Regex MultiSpaceRegex();
}

/// <summary>Thread-safe page→hit accumulator used while OCR pages complete in parallel.</summary>
public sealed class EntityAccumulator
{
    internal ConcurrentDictionary<string, PageHits> Companies { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, PageHits> Persons { get; } = new(StringComparer.Ordinal);

    public void AddCompany(string name, int page) => Add(Companies, name, page);
    public void AddPerson(string name, int page) => Add(Persons, name, page);

    private static void Add(ConcurrentDictionary<string, PageHits> map, string name, int page)
    {
        PageHits hits = map.GetOrAdd(name, static _ => new PageHits());
        lock (hits)
        {
            hits.Count++;
            hits.Pages.Add(page);
        }
    }
}

internal sealed class PageHits
{
    public int Count;
    public HashSet<int> Pages { get; } = [];
}
