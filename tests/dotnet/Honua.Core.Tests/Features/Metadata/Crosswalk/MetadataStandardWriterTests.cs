// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Crosswalk;

[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataStandardWriterTests
{
    private const string ItemUrl = "https://maps.example.org/ogc/records/collections/catalog/items/dataset.trails";
    private static readonly XNamespace Gmd = "http://www.isotc211.org/2005/gmd";
    private static readonly XNamespace Mri = "http://standards.iso.org/iso/19115/-3/mri/1.0";
    private static readonly XNamespace Mdb = "http://standards.iso.org/iso/19115/-3/mdb/2.0";

    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData(MetadataStandard.Iso19139)]
    [InlineData(MetadataStandard.Iso19115Part3)]
    public void Write_FullyPopulatedDataset_EmitsEveryCrosswalkTarget(MetadataStandard standard)
    {
        var document = standard == MetadataStandard.Iso19139
            ? Iso19139Writer.Write(CrosswalkFixtures.FullRecord())
            : Iso19115Part3Writer.Write(CrosswalkFixtures.FullRecord());
        var names = document.Descendants().Select(element => element.Name.LocalName).ToHashSet(StringComparer.Ordinal);

        var missing = MetadataCrosswalk.Fields
            .Where(field => field.Target(standard) is not null)
            .Where(field => !names.Contains(LastElement(field.Target(standard)!)))
            .Select(field => field.Field);

        missing.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Write_FullyPopulatedDataset_DcatCarriesEveryCrosswalkTarget()
    {
        var json = DcatApWriter.Write(CrosswalkFixtures.FullRecord(), ItemUrl);
        var distribution = json["dcat:distribution"]![0]!.AsObject();

        var missing = MetadataCrosswalk.Fields
            .Where(field => field.Target(MetadataStandard.DcatAp) is { } target
                            && !json.ContainsKey(target.Split('/')[0])
                            && !distribution.ContainsKey(target.Split('/')[0]))
            .Select(field => field.Field);

        missing.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Iso19139_Dataset_UsesIso639AndDropsCodesThe2007ListsLack()
    {
        var document = Iso19139Writer.Write(CrosswalkFixtures.FullRecord());

        document.Root!.Element(Gmd + "language")!.Element(Gmd + "LanguageCode")!.Attribute("codeListValue")!.Value.Should().Be("eng");
        document.Descendants(Gmd + "MD_TopicCategoryCode").Select(element => element.Value).Should().Equal("transportation");
        document.Descendants(Gmd + "CI_RoleCode").Select(element => element.Value).Should().NotContain("rightsHolder");
        document.Descendants(Gmd + "otherConstraints").Select(element => element.Value)
            .Should().Contain("Public, no restrictions.").And.Contain(value => value.Contains("CC-BY-4.0"));
        document.Descendants(Gmd + "statement").Single().Value.Should().Be("Digitized from 2024 orthoimagery.");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Iso19115Part3_Dataset_KeepsIso19115Part1CodesAndTemporalResolution()
    {
        var document = Iso19115Part3Writer.Write(CrosswalkFixtures.FullRecord());

        document.Root!.Name.Should().Be(Mdb + "MD_Metadata");
        document.Descendants(Mri + "MD_TopicCategoryCode").Select(element => element.Value).Should().Equal("transportation", "disaster");
        document.Descendants().Where(element => element.Name.LocalName == "CI_RoleCode").Select(element => element.Value)
            .Should().Contain("rightsHolder");
        document.Descendants(Mri + "temporalResolution").Single().Value.Should().Be("P1D");
        document.Descendants(Mri + "credit").Single().Value.Should().Be("County GIS");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsoWriters_Service_AreRefused()
    {
        var graph = CrosswalkFixtures.Graph();
        var service = DescriptiveMetadataResolver.ForService(graph, graph.Services[0]);

        FluentActions.Invoking(() => Iso19139Writer.Write(service)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => Iso19115Part3Writer.Write(service)).Should().Throw<NotSupportedException>();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Dcat_Dataset_UsesEuVocabulariesAndLicensesTheDistribution()
    {
        var json = DcatApWriter.Write(CrosswalkFixtures.FullRecord(), ItemUrl);

        json["@type"]!.GetValue<string>().Should().Be("dcat:Dataset");
        json["dct:language"]!["@id"]!.GetValue<string>().Should().Be("http://publications.europa.eu/resource/authority/language/ENG");
        json["dct:accrualPeriodicity"]!["@id"]!.GetValue<string>().Should().EndWith("/frequency/MONTHLY");
        json["dcat:theme"]!.AsArray().Select(theme => theme!["@id"]!.GetValue<string>()).Should().Equal(
            "http://publications.europa.eu/resource/authority/data-theme/TRAN", "http://inspire.ec.europa.eu/theme/tn");
        json["dcat:contactPoint"]![0]!["vcard:hasEmail"]!["@id"]!.GetValue<string>().Should().Be("mailto:gis@example.org");
        json["dcat:distribution"]![0]!["dct:license"]!["@id"]!.GetValue<string>().Should().Contain("CC-BY-4.0");
        json.ContainsKey("dct:license").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Dcat_Service_IsADataServiceWithEndpoint()
    {
        var graph = CrosswalkFixtures.Graph();
        var service = DescriptiveMetadataResolver.ForService(graph, graph.Services[0]) with
        {
            Distributions = [new DescriptiveDistribution("https://maps.example.org/ogc/services/parks/wms", "OGC:WMS")],
        };

        var json = DcatApWriter.Write(service, "https://maps.example.org/services/parks");

        json["@type"]!.GetValue<string>().Should().Be("dcat:DataService");
        json["dcat:endpointURL"]!["@id"]!.GetValue<string>().Should().EndWith("/wms");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ThemeSchemes_UrnAndHashIris_SplitWithoutFailing()
    {
        var schemes = MetadataCrosswalk.ThemeSchemes(["urn:example", "https://example.org/themes#hy", "https://example.org/"]);

        schemes.Should().ContainSingle();
        schemes[0].Scheme.Should().Be("https://example.org/themes");
        schemes[0].Concepts.Should().Equal("hy");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Fields_EveryRow_MapsToAtLeastOneStandard()
    {
        MetadataCrosswalk.Fields.Should().OnlyContain(field => field.Targets.Values.Any(target => target != null));
        MetadataCrosswalk.Fields.Select(field => field.Field).Should().OnlyHaveUniqueItems();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void ObjectMetadata_NewDescriptiveFields_RoundTripThroughTheGraphContext()
    {
        var json = JsonSerializer.Serialize(CrosswalkFixtures.FullDataset, MetadataV2JsonContext.Default.MetadataV2ObjectMetadata);

        var restored = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2ObjectMetadata)!;

        restored.Contacts.Should().HaveCount(2);
        restored.TopicCategories.Should().Equal("transportation", "disaster");
        restored.SpatialResolution!.DistanceMeters.Should().Be(0.5);
        JsonNode.Parse(json)!["accessConstraints"]!.GetValue<string>().Should().Be("Public, no restrictions.");
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void GraphValidation_UnknownCodeValues_AreRejected()
    {
        var graph = CrosswalkFixtures.Graph(resource: CrosswalkFixtures.FullDataset with
        {
            TopicCategories = ["roads"],
            UpdateFrequency = "hourly",
            TemporalResolution = "P99999999999Y",
            Lineage = "line\u0001",
        });

        var errors = MetadataV2GraphValidator.Validate(graph).Errors;

        errors.Should().Contain(error => error.Contains("metadata.topicCategories") && error.Contains("'roads'"));
        errors.Should().Contain(error => error.Contains("metadata.updateFrequency"));
        errors.Should().Contain(error => error.Contains("metadata.temporalResolution"));
        errors.Should().Contain(error => error.Contains("metadata.lineage"));
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void GraphValidation_FolderGovernance_RejectsInvalidInheritedText()
    {
        var graph = CrosswalkFixtures.Graph(folder: new MetadataV2ObjectMetadata
        {
            Id = "folder.parks", Name = "parks", Fees = "fee\u0001",
        });

        MetadataV2GraphValidator.Validate(graph).Errors.Should()
            .Contain(error => error.Contains("folder 'folder.parks' metadata.fees"));
    }

    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData("not-an-email", "https://example.org/contact", false)]
    [InlineData("gis@example.org", "javascript:alert(1)", false)]
    [InlineData("gis@example.org", "https://user:secret@example.org/contact", false)]
    [InlineData("gis@example.org", "https://example.org/contact", true)]
    public void GraphValidation_ResponsibleParty_UsesPublicContactRules(string email, string url, bool valid)
    {
        var graph = CrosswalkFixtures.Graph(resource: CrosswalkFixtures.FullDataset with
        {
            Contacts = [new MetadataV2ResponsibleParty { Email = email, Url = url }],
        });

        var errors = MetadataV2GraphValidator.Validate(graph).Errors
            .Where(error => error.Contains("metadata.contacts"));

        errors.Any().Should().Be(!valid);
    }

    [UnitTheory]
    [Operation(Operations.Metadata)]
    [InlineData(MetadataStandard.Iso19139)]
    [InlineData(MetadataStandard.Iso19115Part3)]
    public void IsoWriters_NearZeroCoordinates_UseXmlSchemaDecimal(MetadataStandard standard)
    {
        var metadata = CrosswalkFixtures.FullRecord() with
        {
            Extent = new MetadataV2Bbox { West = -1e-7, South = 1e-8, East = 1e-7, North = 1e-6 },
        };
        var document = standard == MetadataStandard.Iso19139
            ? Iso19139Writer.Write(metadata) : Iso19115Part3Writer.Write(metadata);
        var decimalType = System.Xml.Schema.XmlSchemaType.GetBuiltInSimpleType(System.Xml.Schema.XmlTypeCode.Decimal);
        var coordinates = document.Descendants().Where(element => element.Name.LocalName == "Decimal").ToArray();

        coordinates.Should().HaveCount(4);
        foreach (var coordinate in coordinates)
        {
            FluentActions.Invoking(() => decimalType.Datatype!.ParseValue(
                coordinate.Value, new System.Xml.NameTable(), null!)).Should().NotThrow();
        }
        coordinates.Select(coordinate => decimal.Parse(coordinate.Value, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal(-0.0000001m, 0.0000001m, 0.00000001m, 0.000001m);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Iso19139_DatasetPublisher_IsPreservedAlongsideContact()
    {
        var document = Iso19139Writer.Write(CrosswalkFixtures.FullRecord());
        var publisher = document.Descendants(Gmd + "CI_ResponsibleParty")
            .Single(party => party.Element(Gmd + "role")!.Value == "publisher");

        publisher.Element(Gmd + "organisationName")!.Value.Should().Be("County of Example");
        document.Descendants(Gmd + "individualName").Select(element => element.Value).Should().Contain("Ana Ruiz");
    }

    private static string LastElement(string path)
    {
        var last = path.Split('/')[^1];
        var bracket = last.IndexOf('[', StringComparison.Ordinal);
        last = bracket < 0 ? last : last[..bracket];
        return last[(last.IndexOf(':', StringComparison.Ordinal) + 1)..];
    }
}
