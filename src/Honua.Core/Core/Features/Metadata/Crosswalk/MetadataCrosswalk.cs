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

    private static readonly FrozenDictionary<string, (string Bibliographic, string Terminology)> Languages = CreateLanguages();

    private static FrozenDictionary<string, (string Bibliographic, string Terminology)> CreateLanguages()
    {
        // ISO 639-2 terminology:alpha-2:bibliographic; omit the reserved local-use range.
        // Source: https://www.loc.gov/standards/iso639-2/php/code_list.php
        const string registeredCodes = """
            aar:aa:aar abk:ab:abk ace::ace ach::ach ada::ada ady::ady afa::afa afh::afh
            afr:af:afr ain::ain aka:ak:aka akk::akk ale::ale alg::alg alt::alt amh:am:amh
            ang::ang anp::anp apa::apa ara:ar:ara arc::arc arg:an:arg arn::arn arp::arp
            art::art arw::arw asm:as:asm ast::ast ath::ath aus::aus ava:av:ava ave:ae:ave
            awa::awa aym:ay:aym aze:az:aze bad::bad bai::bai bak:ba:bak bal::bal bam:bm:bam
            ban::ban bas::bas bat::bat bej::bej bel:be:bel bem::bem ben:bn:ben ber::ber
            bho::bho bih:bh:bih bik::bik bin::bin bis:bi:bis bla::bla bnt::bnt bod:bo:tib
            bos:bs:bos bra::bra bre:br:bre btk::btk bua::bua bug::bug bul:bg:bul byn::byn
            cad::cad cai::cai car::car cat:ca:cat cau::cau ceb::ceb cel::cel ces:cs:cze
            cha:ch:cha chb::chb che:ce:che chg::chg chk::chk chm::chm chn::chn cho::cho
            chp::chp chr::chr chu:cu:chu chv:cv:chv chy::chy cmc::cmc cnr::cnr cop::cop
            cor:kw:cor cos:co:cos cpe::cpe cpf::cpf cpp::cpp cre:cr:cre crh::crh crp::crp
            csb::csb cus::cus cym:cy:wel dak::dak dan:da:dan dar::dar day::day del::del
            den::den deu:de:ger dgr::dgr din::din div:dv:div doi::doi dra::dra dsb::dsb
            dua::dua dum::dum dyu::dyu dzo:dz:dzo efi::efi egy::egy eka::eka ell:el:gre
            elx::elx eng:en:eng enm::enm epo:eo:epo est:et:est eus:eu:baq ewe:ee:ewe ewo::ewo
            fan::fan fao:fo:fao fas:fa:per fat::fat fij:fj:fij fil::fil fin:fi:fin fiu::fiu
            fon::fon fra:fr:fre frm::frm fro::fro frr::frr frs::frs fry:fy:fry ful:ff:ful
            fur::fur gaa::gaa gay::gay gba::gba gem::gem gez::gez gil::gil gla:gd:gla
            gle:ga:gle glg:gl:glg glv:gv:glv gmh::gmh goh::goh gon::gon gor::gor got::got
            grb::grb grc::grc grn:gn:grn gsw::gsw guj:gu:guj gwi::gwi hai::hai hat:ht:hat
            hau:ha:hau haw::haw heb:he:heb her:hz:her hil::hil him::him hin:hi:hin hit::hit
            hmn::hmn hmo:ho:hmo hrv:hr:hrv hsb::hsb hun:hu:hun hup::hup hye:hy:arm iba::iba
            ibo:ig:ibo ido:io:ido iii:ii:iii ijo::ijo iku:iu:iku ile:ie:ile ilo::ilo ina:ia:ina
            inc::inc ind:id:ind ine::ine inh::inh ipk:ik:ipk ira::ira iro::iro isl:is:ice
            ita:it:ita jav:jv:jav jbo::jbo jpn:ja:jpn jpr::jpr jrb::jrb kaa::kaa kab::kab
            kac::kac kal:kl:kal kam::kam kan:kn:kan kar::kar kas:ks:kas kat:ka:geo kau:kr:kau
            kaw::kaw kaz:kk:kaz kbd::kbd kha::kha khi::khi khm:km:khm kho::kho kik:ki:kik
            kin:rw:kin kir:ky:kir kmb::kmb kok::kok kom:kv:kom kon:kg:kon kor:ko:kor kos::kos
            kpe::kpe krc::krc krl::krl kro::kro kru::kru kua:kj:kua kum::kum kur:ku:kur
            kut::kut lad::lad lah::lah lam::lam lao:lo:lao lat:la:lat lav:lv:lav lez::lez
            lim:li:lim lin:ln:lin lit:lt:lit lol::lol loz::loz ltz:lb:ltz lua::lua lub:lu:lub
            lug:lg:lug lui::lui lun::lun luo::luo lus::lus mad::mad mag::mag mah:mh:mah
            mai::mai mak::mak mal:ml:mal man::man map::map mar:mr:mar mas::mas mdf::mdf
            mdr::mdr men::men mga::mga mic::mic min::min mis::mis mkd:mk:mac mkh::mkh
            mlg:mg:mlg mlt:mt:mlt mnc::mnc mni::mni mno::mno moh::moh mon:mn:mon mos::mos
            mri:mi:mao msa:ms:may mul::mul mun::mun mus::mus mwl::mwl mwr::mwr mya:my:bur
            myn::myn myv::myv nah::nah nai::nai nap::nap nau:na:nau nav:nv:nav nbl:nr:nbl
            nde:nd:nde ndo:ng:ndo nds::nds nep:ne:nep new::new nia::nia nic::nic niu::niu
            nld:nl:dut nno:nn:nno nob:nb:nob nog::nog non::non nor:no:nor nqo::nqo nso::nso
            nub::nub nwc::nwc nya:ny:nya nym::nym nyn::nyn nyo::nyo nzi::nzi oci:oc:oci
            oji:oj:oji ori:or:ori orm:om:orm osa::osa oss:os:oss ota::ota oto::oto paa::paa
            pag::pag pal::pal pam::pam pan:pa:pan pap::pap pau::pau peo::peo phi::phi
            phn::phn pli:pi:pli pol:pl:pol pon::pon por:pt:por pra::pra pro::pro pus:ps:pus
            que:qu:que raj::raj rap::rap rar::rar roa::roa roh:rm:roh rom::rom ron:ro:rum
            run:rn:run rup::rup rus:ru:rus sad::sad sag:sg:sag sah::sah sai::sai sal::sal
            sam::sam san:sa:san sas::sas sat::sat scn::scn sco::sco sel::sel sem::sem
            sga::sga sgn::sgn shn::shn sid::sid sin:si:sin sio::sio sit::sit sla::sla
            slk:sk:slo slv:sl:slv sma::sma sme:se:sme smi::smi smj::smj smn::smn smo:sm:smo
            sms::sms sna:sn:sna snd:sd:snd snk::snk sog::sog som:so:som son::son sot:st:sot
            spa:es:spa sqi:sq:alb srd:sc:srd srn::srn srp:sr:srp srr::srr ssa::ssa ssw:ss:ssw
            suk::suk sun:su:sun sus::sus sux::sux swa:sw:swa swe:sv:swe syc::syc syr::syr
            tah:ty:tah tai::tai tam:ta:tam tat:tt:tat tel:te:tel tem::tem ter::ter tet::tet
            tgk:tg:tgk tgl:tl:tgl tha:th:tha tig::tig tir:ti:tir tiv::tiv tkl::tkl tlh::tlh
            tli::tli tmh::tmh tog::tog ton:to:ton tpi::tpi tsi::tsi tsn:tn:tsn tso:ts:tso
            tuk:tk:tuk tum::tum tup::tup tur:tr:tur tut::tut tvl::tvl twi:tw:twi tyv::tyv
            udm::udm uga::uga uig:ug:uig ukr:uk:ukr umb::umb und::und urd:ur:urd uzb:uz:uzb
            vai::vai ven:ve:ven vie:vi:vie vol:vo:vol vot::vot wak::wak wal::wal war::war
            was::was wen::wen wln:wa:wln wol:wo:wol xal::xal xho:xh:xho yao::yao yap::yap
            yid:yi:yid yor:yo:yor ypk::ypk zap::zap zbl::zbl zen::zen zgh::zgh zha:za:zha
            zho:zh:chi znd::znd zul:zu:zul zun::zun zxx::zxx zza::zza
            """;
        var languages = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in registeredCodes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var codes = entry.Split(':');
            var value = (codes[2], codes[0]);
            languages[codes[0]] = value;
            languages[codes[2]] = value;
            if (codes[1].Length > 0)
            {
                languages[codes[1]] = value;
            }
        }
        return languages.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

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
        return Languages.TryGetValue(primary, out var codes) ? codes : null;
    }

    // Targets are listed in MetadataStandard declaration order.
    private static CrosswalkField Row(string field, params string?[] targets)
        => new(field, Enum.GetValues<MetadataStandard>().Zip(targets).ToDictionary(pair => pair.First, pair => pair.Second));
}
