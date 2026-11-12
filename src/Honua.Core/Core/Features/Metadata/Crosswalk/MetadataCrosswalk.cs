// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Core.Features.Metadata.Crosswalk;

public enum MetadataStandard
{
    Iso19115Part3,
    Iso19139,
    DcatAp,
    Stac,
    OgcRecords,
    OgcCapabilities,
}

public sealed record ThemeScheme(string Scheme, IReadOnlyList<string> Concepts);

// A null target means the standard has no slot for the field; the encoder drops it.
public sealed record CrosswalkField(string Field, IReadOnlyDictionary<MetadataStandard, string?> Targets)
{
    public string? Target(MetadataStandard standard) => Targets.GetValueOrDefault(standard);
}

public static class MetadataCrosswalk
{
    public static IReadOnlyList<CrosswalkField> Fields { get; } =
    [
        Row("title", "mri:citation/cit:CI_Citation/cit:title", "gmd:citation/gmd:CI_Citation/gmd:title", "dct:title", "title", "properties.title", "Title"),
        Row("description", "mri:abstract", "gmd:abstract", "dct:description", "description", "properties.description", "Abstract"),
        Row("keywords", "mri:descriptiveKeywords/mri:MD_Keywords/mri:keyword", "gmd:descriptiveKeywords/gmd:MD_Keywords/gmd:keyword", "dcat:keyword", "keywords", "properties.keywords", "Keywords"),
        Row("themes", "mri:descriptiveKeywords/mri:MD_Keywords[type=theme]", "gmd:descriptiveKeywords/gmd:MD_Keywords[type=theme]", "dcat:theme", "themes", "properties.themes", null),
        Row("topicCategories", "mri:topicCategory", "gmd:topicCategory", null, null, null, null),
        Row("language", "mri:defaultLocale/lan:PT_Locale/lan:language", "gmd:language", "dct:language", "language", "properties.language", null),
        Row("license", "mri:resourceConstraints/mco:MD_LegalConstraints/mco:useConstraints", "gmd:resourceConstraints/gmd:MD_LegalConstraints/gmd:useConstraints", "dct:license", "license", "properties.license", "AccessConstraints"),
        Row("accessConstraints", "mri:resourceConstraints/mco:MD_LegalConstraints/mco:accessConstraints", "gmd:resourceConstraints/gmd:MD_LegalConstraints/gmd:accessConstraints", "dct:rights", null, "properties.rights", "AccessConstraints"),
        Row("fees", null, null, null, null, null, "Fees"),
        Row("attribution", "mri:credit", "gmd:credit", null, "attribution", null, "Attribution"),
        Row("publisher", "cit:CI_Responsibility[role=publisher]", "gmd:CI_ResponsibleParty[role=publisher]", "dct:publisher", "providers[roles=host]", "properties.contacts[roles=publisher]", "ProviderName"),
        Row("contacts", "mri:pointOfContact/cit:CI_Responsibility", "gmd:pointOfContact/gmd:CI_ResponsibleParty", "dcat:contactPoint", "providers", "properties.contacts", "ServiceContact"),
        Row("lineage", "mdb:resourceLineage/mrl:LI_Lineage/mrl:statement", "gmd:dataQualityInfo/gmd:DQ_DataQuality/gmd:lineage/gmd:LI_Lineage/gmd:statement", "dct:provenance", null, null, null),
        Row("updateFrequency", "mri:resourceMaintenance/mmi:MD_MaintenanceInformation/mmi:maintenanceAndUpdateFrequency", "gmd:resourceMaintenance/gmd:MD_MaintenanceInformation/gmd:maintenanceAndUpdateFrequency", "dct:accrualPeriodicity", null, null, null),
        Row("spatialResolution", "mri:spatialResolution/mri:MD_Resolution", "gmd:spatialResolution/gmd:MD_Resolution", "dcat:spatialResolutionInMeters", "gsd", null, null),
        Row("temporalResolution", "mri:temporalResolution", null, "dcat:temporalResolution", null, null, null),
        Row("extent", "mri:extent/gex:EX_Extent/gex:geographicElement", "gmd:extent/gmd:EX_Extent/gmd:geographicElement", "dct:spatial/dcat:bbox", "extent.spatial", "geometry", "WGS84BoundingBox"),
        Row("temporalExtent", "mri:extent/gex:EX_Extent/gex:temporalElement", "gmd:extent/gmd:EX_Extent/gmd:temporalElement", "dct:temporal", "extent.temporal", "time", null),
        Row("crs", "mdb:referenceSystemInfo", "gmd:referenceSystemInfo", null, "crs", null, "CRS"),
        Row("distributions", "mdb:distributionInfo/mrd:MD_Distribution/mrd:transferOptions", "gmd:distributionInfo/gmd:MD_Distribution/gmd:transferOptions", "dcat:distribution", "links", "links", null),
        Row("metadataLink", null, null, "foaf:page", "links[rel=describedby]", "links[rel=describedby]", "MetadataURL"),
    ];

    private static readonly FrozenDictionary<string, (string Bibliographic, string Terminology)> Languages =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ar"] = ("ara", "ara"),
            ["bg"] = ("bul", "bul"),
            ["ca"] = ("cat", "cat"),
            ["cs"] = ("cze", "ces"),
            ["cy"] = ("wel", "cym"),
            ["da"] = ("dan", "dan"),
            ["de"] = ("ger", "deu"),
            ["el"] = ("gre", "ell"),
            ["en"] = ("eng", "eng"),
            ["es"] = ("spa", "spa"),
            ["et"] = ("est", "est"),
            ["eu"] = ("baq", "eus"),
            ["fa"] = ("per", "fas"),
            ["fi"] = ("fin", "fin"),
            ["fr"] = ("fre", "fra"),
            ["ga"] = ("gle", "gle"),
            ["gl"] = ("glg", "glg"),
            ["he"] = ("heb", "heb"),
            ["hi"] = ("hin", "hin"),
            ["hr"] = ("hrv", "hrv"),
            ["hu"] = ("hun", "hun"),
            ["id"] = ("ind", "ind"),
            ["is"] = ("ice", "isl"),
            ["it"] = ("ita", "ita"),
            ["ja"] = ("jpn", "jpn"),
            ["ko"] = ("kor", "kor"),
            ["lt"] = ("lit", "lit"),
            ["lv"] = ("lav", "lav"),
            ["mi"] = ("mao", "mri"),
            ["ms"] = ("may", "msa"),
            ["mt"] = ("mlt", "mlt"),
            ["nb"] = ("nob", "nob"),
            ["nl"] = ("dut", "nld"),
            ["nn"] = ("nno", "nno"),
            ["no"] = ("nor", "nor"),
            ["pl"] = ("pol", "pol"),
            ["pt"] = ("por", "por"),
            ["ro"] = ("rum", "ron"),
            ["ru"] = ("rus", "rus"),
            ["sk"] = ("slo", "slk"),
            ["sl"] = ("slv", "slv"),
            ["sr"] = ("srp", "srp"),
            ["sv"] = ("swe", "swe"),
            ["sw"] = ("swa", "swa"),
            ["th"] = ("tha", "tha"),
            ["tr"] = ("tur", "tur"),
            ["uk"] = ("ukr", "ukr"),
            ["vi"] = ("vie", "vie"),
            ["zh"] = ("chi", "zho"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // ISO maintenance frequency -> EU frequency authority table code; values without an equivalent are dropped.
    private static readonly FrozenDictionary<string, string> EuFrequencies = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["continual"] = "UPDATE_CONT",
        ["daily"] = "DAILY",
        ["weekly"] = "WEEKLY",
        ["fortnightly"] = "BIWEEKLY",
        ["monthly"] = "MONTHLY",
        ["quarterly"] = "QUARTERLY",
        ["biannually"] = "ANNUAL_2",
        ["annually"] = "ANNUAL",
        ["biennially"] = "BIENNIAL",
        ["irregular"] = "IRREG",
        ["unknown"] = "UNKNOWN",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public const string UndeterminedLanguage = "und";

    public const string EuDataThemeScheme = "http://publications.europa.eu/resource/authority/data-theme";

    private static readonly FrozenSet<string> EuDataThemes = FrozenSet.ToFrozenSet(
        ["AGRI", "ECON", "EDUC", "ENER", "ENVI", "GOVE", "HEAL", "INTR", "JUST", "OP_DATPRO", "REGI", "SOCI", "TECH", "TRAN"],
        StringComparer.Ordinal);

    // A theme is an absolute concept IRI or a code from the EU data-theme table; other values have no scheme to publish.
    public static string? ThemeIri(string theme)
    {
        if (Uri.IsWellFormedUriString(theme, UriKind.Absolute))
        {
            return theme;
        }
        var code = theme.Trim().ToUpperInvariant();
        return EuDataThemes.Contains(code) ? $"{EuDataThemeScheme}/{code}" : null;
    }

    public static IReadOnlyList<ThemeScheme> ThemeSchemes(IReadOnlyList<string> themes)
        => themes.Select(ThemeIri)
            .OfType<string>()
            .Select(iri => (Iri: iri, Split: iri.LastIndexOfAny(['/', '#'])))
            .Where(theme => theme.Split > theme.Iri.IndexOf(':', StringComparison.Ordinal) && theme.Split < theme.Iri.Length - 1)
            .Select(theme => (Scheme: theme.Iri[..theme.Split], Concept: theme.Iri[(theme.Split + 1)..]))
            .GroupBy(theme => theme.Scheme, StringComparer.Ordinal)
            .Select(group => new ThemeScheme(group.Key, group.Select(theme => theme.Concept).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();

    // ISO 19139 and ISO 19115-3 use the ISO 639-2 bibliographic code.
    public static string IsoLanguage(string? language) => LanguageCodes(language)?.Bibliographic ?? UndeterminedLanguage;

    // The EU language authority table uses the ISO 639-2 terminology code.
    public static string? EuLanguageCode(string? language) => LanguageCodes(language)?.Terminology.ToUpperInvariant();

    public static string? EuFrequencyCode(string? updateFrequency)
        => updateFrequency is null ? null : EuFrequencies.GetValueOrDefault(updateFrequency);

    private static (string Bibliographic, string Terminology)? LanguageCodes(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }
        var primary = language.Split('-', 2)[0];
        if (Languages.TryGetValue(primary, out var codes))
        {
            return codes;
        }
        var match = Languages.Values.FirstOrDefault(codes =>
            string.Equals(codes.Bibliographic, primary, StringComparison.OrdinalIgnoreCase)
            || string.Equals(codes.Terminology, primary, StringComparison.OrdinalIgnoreCase));
        return match == default ? null : match;
    }

    // Targets are listed in MetadataStandard declaration order.
    private static CrosswalkField Row(string field, params string?[] targets)
        => new(field, Enum.GetValues<MetadataStandard>().Zip(targets).ToDictionary(pair => pair.First, pair => pair.Second));
}
