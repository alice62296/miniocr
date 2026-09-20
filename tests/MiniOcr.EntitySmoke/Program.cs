using MiniOcr.Models;
using MiniOcr.Services;

int failed = 0;

void AssertTrue(bool cond, string msg)
{
    if (cond)
    {
        Console.WriteLine("  PASS  " + msg);
        return;
    }

    Console.WriteLine("  FAIL  " + msg);
    failed++;
}

void AssertContains(IEnumerable<EntityHit> hits, string name, string msg)
{
    bool ok = hits.Any(h => h.Name == name);
    AssertTrue(ok, msg + $" (expect '{name}', got: [{string.Join(", ", hits.Select(h => h.Name))}])");
}

Console.WriteLine("=== EntityExtractor smoke ===");

string page1 =
    """
    合同编号：HT-2026-001
    甲方：北京华为技术有限公司
    乙方：上海浦东发展银行股份有限公司
    法定代表人：张伟
    联系人：李娜女士
    英文方：Acme Trading Ltd.
    Signed by: John Smith
    """;

string page2 =
    """
    公司名称：深圳市腾讯计算机系统有限公司
    负责人：王芳
    经办人：欧阳锋
    另见 Microsoft Corporation 与 中国工商银行
    出席：陈晓东经理、赵丽
    """;

string page3 =
    """
    本页无实体，仅有说明文字与日期 2026年9月21日。
    """;

OcrEntities entities = EntityExtractor.ExtractFromPages([page1, page2, page3]);

Console.WriteLine("Companies:");
foreach (EntityHit h in entities.Companies)
    Console.WriteLine($"  - {h.Name}  pages=[{string.Join(",", h.Pages)}] count={h.Count}");

Console.WriteLine("Persons:");
foreach (EntityHit h in entities.Persons)
    Console.WriteLine($"  - {h.Name}  pages=[{string.Join(",", h.Pages)}] count={h.Count}");

AssertContains(entities.Companies, "北京华为技术有限公司", "label 甲方 company");
AssertContains(entities.Companies, "上海浦东发展银行股份有限公司", "suffix 股份有限公司");
AssertContains(entities.Companies, "深圳市腾讯计算机系统有限公司", "label 公司名称");
AssertContains(entities.Companies, "Acme Trading Ltd.", "English Ltd");
AssertContains(entities.Companies, "Microsoft Corporation", "English Corporation");
AssertContains(entities.Companies, "中国工商银行", "suffix 银行");
AssertTrue(!entities.Companies.Any(c => c.Name == "上海浦东发展银行"),
    "shorter 银行 prefix suppressed when 股份有限公司 form exists");

AssertContains(entities.Persons, "张伟", "label 法定代表人");
AssertContains(entities.Persons, "李娜", "title 女士");
AssertContains(entities.Persons, "王芳", "label 负责人");
AssertContains(entities.Persons, "欧阳锋", "compound surname");
AssertContains(entities.Persons, "陈晓东", "title 经理");
AssertContains(entities.Persons, "John Smith", "English person");

// Page refs / dedup
EntityHit? hw = entities.Companies.FirstOrDefault(c => c.Name.Contains("华为", StringComparison.Ordinal));
AssertTrue(hw is not null && hw.Pages.Contains(1), "华为 page ref includes 1");

EntityHit? tw = entities.Companies.FirstOrDefault(c => c.Name.Contains("腾讯", StringComparison.Ordinal));
AssertTrue(tw is not null && tw.Pages.Contains(2), "腾讯 page ref includes 2");

// Should not invent entities on empty-ish page
AssertTrue(!entities.Persons.Any(p => p.Pages.Contains(3) && p.Pages.Count == 1 && p.Count == 1 && p.Name.Length == 2 && page3.Contains(p.Name)),
    "page3 should not be a major person source");

// Org context: person inside company name should be suppressed when possible
OcrEntities orgTrap = EntityExtractor.ExtractFromPages(
[
    "买方为李宁体育用品有限公司，联系人：周杰。",
]);
AssertContains(orgTrap.Companies, "李宁体育用品有限公司", "org trap company");
AssertContains(orgTrap.Persons, "周杰", "org trap real person");
AssertTrue(!orgTrap.Persons.Any(p => p.Name == "李宁"), "should not extract 李宁 from 李宁体育用品有限公司");

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine("All smoke checks passed.");
    return 0;
}

Console.WriteLine($"FAILED: {failed} assertion(s).");
return 1;
