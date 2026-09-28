using MiniOcr.Models;

namespace MiniOcr.Services;

/// <summary>
/// Turns raw LLM name lists into protocol entities.
/// Drops strings that cannot be aligned to the OCR text, masked names, roles,
/// courts and other non-companies, and short names that are only a prefix of a
/// longer legal name. Does not invent names with a regex scan.
/// </summary>
public static class EntityPostProcessor
{
    public static OcrEntities Merge(
        IReadOnlyList<OcrPageResult> pages,
        IEnumerable<string> companies,
        IEnumerable<string> persons)
    {
        List<string> companyNames = Collect(companies, person: false);
        List<string> personNames = Collect(persons, person: true);

        companyNames = Dedup(companyNames.Select(name => ExpandLegalForm(pages, name)));
        companyNames = companyNames.Where(name => HasHit(pages, name)).ToList();
        personNames = personNames.Where(name => HasHit(pages, name)).ToList();
        companyNames = SuppressShortCompanies(companyNames);
        personNames = personNames.Where(name => HasUncoveredPersonHit(pages, name, companyNames, personNames)).ToList();
        companyNames = companyNames.Where(name => HasUncoveredCompanyHit(pages, name, companyNames)).ToList();

        return new OcrEntities
        {
            Companies = BuildHits(pages, companyNames, companyNames),
            Persons = BuildHits(pages, personNames, CoverNamesForPersons(personNames, companyNames)),
        };
    }

    public static bool IsAcceptablePerson(string name)
    {
        if (name.Length is < 2 or > 40)
            return false;
        if (name.Contains('某', StringComparison.Ordinal))
            return false;
        if (RoleWords.Contains(name))
            return false;
        if (IsAcceptableCompany(name))
            return false;

        int cjk = 0;
        int letters = 0;
        foreach (char c in name)
        {
            if (EntityText.IsCjk(c))
                cjk++;
            else if (char.IsLetter(c))
                letters++;
        }

        if (cjk == 0 && letters == 0)
            return false;
        if (letters == 0 && cjk < 2)
            return false;
        if (letters == 0 && !name.Contains('·', StringComparison.Ordinal) && cjk > 6)
            return false;
        if (letters == 0 && name.Contains('·', StringComparison.Ordinal) && cjk > 16)
            return false;
        return true;
    }

    public static bool IsAcceptableCompany(string name)
    {
        if (name.Length < 4 || name.Length > 80)
            return false;
        if (GenericCompanies.Contains(name))
            return false;
        if (ContainsGovernment(name) && !name.Contains("公司", StringComparison.Ordinal))
            return false;
        if (EndsWithInstitution(name) && !name.Contains("公司", StringComparison.Ordinal))
            return false;
        if (EndsWithChineseOrg(name))
            return true;
        return EntityText.EndsWithAbbrevToken(name.EndsWith('.') ? name[..^1] : name);
    }
