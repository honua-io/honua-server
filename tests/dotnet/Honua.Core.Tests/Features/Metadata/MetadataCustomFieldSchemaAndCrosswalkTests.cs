// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Crosswalk;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Core.Tests.Features.Metadata;

public sealed class MetadataCustomFieldSchemaAndCrosswalkTests
{
    [UnitTest]
    public void ValidateSchemas_AcceptsValidDefinitionsAndRejectsInvalidOnes()
    {
        var validErrors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateSchemas(validErrors,
        [
            new MetadataV2CustomFieldSchema
            {
                Name = "steward_department",
                Title = "Steward Department",
                Type = MetadataV2CustomFieldTypes.EnumValue,
                Required = true,
                AllowedValues = ["Planning", "PublicWorks"],
                AppliesTo = [MetadataV2CustomFieldKinds.Dataset, MetadataV2CustomFieldKinds.Service],
                Facetable = true,
            },
            new MetadataV2CustomFieldSchema
            {
                Name = "review_date",
                Type = MetadataV2CustomFieldTypes.Date,
                AppliesTo = [MetadataV2CustomFieldKinds.Dataset],
            },
        ]);
        validErrors.Should().BeEmpty();

        var invalidErrors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateSchemas(invalidErrors,
        [
            new MetadataV2CustomFieldSchema { Name = "BadName", Type = "unknown" },
            new MetadataV2CustomFieldSchema { Name = "dup", Type = MetadataV2CustomFieldTypes.EnumValue },
            new MetadataV2CustomFieldSchema { Name = "dup", Type = MetadataV2CustomFieldTypes.Text, AppliesTo = ["bogus"] },
        ]);
        invalidErrors.Should().Contain(e => e.Contains("BadName", StringComparison.Ordinal) || e.Contains("lowercase ASCII", StringComparison.Ordinal));
        invalidErrors.Should().Contain(e => e.Contains("unknown", StringComparison.Ordinal));
        invalidErrors.Should().Contain(e => e.Contains("allowedValues is required", StringComparison.Ordinal));
        invalidErrors.Should().Contain(e => e.Contains("duplicated", StringComparison.Ordinal));
        invalidErrors.Should().Contain(e => e.Contains("bogus", StringComparison.Ordinal));
    }

    [UnitTheory]
    [InlineData("steward\n")]
    [InlineData("steward.department")]
    public void ValidateSchemas_RejectsNamesWithTrailingNewlineOrDots(string name)
    {
        var errors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateSchemas(errors, [new MetadataV2CustomFieldSchema { Name = name }]);
        errors.Should().ContainSingle().Which.Should().Contain("lowercase ASCII");
    }

    [UnitTest]
    public void GraphValidation_RequiredCustomFieldMissing_DoesNotBlockOtherSaves()
    {
        var graph = new MetadataV2Graph
        {
            Environment = "prod",
            GeneratedAt = DateTimeOffset.UtcNow,
            CustomFieldSchemas = [ItemSchemas[0]],
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "ds-new", Name = "new_layer", Title = "New layer" },
                    Type = MetadataV2ObjectType.FeatureDataset,
                },
            ],
        };

        MetadataV2GraphValidator.Validate(graph).Errors.Should().NotContain(e => e.Contains("customFields", StringComparison.Ordinal));
    }

    private static readonly MetadataV2CustomFieldSchema[] ItemSchemas =
    [
        new()
        {
            Name = "steward",
            Type = MetadataV2CustomFieldTypes.EnumValue,
            Required = true,
            AllowedValues = ["Planning", "Transit"],
            AppliesTo = [MetadataV2CustomFieldKinds.Dataset],
        },
        new() { Name = "priority", Type = MetadataV2CustomFieldTypes.Number },
        new() { Name = "published", Type = MetadataV2CustomFieldTypes.BooleanValue },
        new() { Name = "reviewed_on", Type = MetadataV2CustomFieldTypes.Date },
        new() { Name = "programs", Type = MetadataV2CustomFieldTypes.TextList },
    ];

    [UnitTest]
    public void ValidateRequiredItemCustomFields_RequiredFieldOnlyAppliesToDeclaredKinds()
    {
        var serviceErrors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateRequiredItemCustomFields(
            serviceErrors, new("svc-1", MetadataV2CustomFieldKinds.Service, null), ItemSchemas);
        serviceErrors.Should().BeEmpty();

        var missingErrors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateRequiredItemCustomFields(
            missingErrors, new("ds-1", MetadataV2CustomFieldKinds.Dataset, null), ItemSchemas);
        missingErrors.Should().ContainSingle().Which.Should().Contain("steward is required");
    }

    [UnitTest]
    public void ValidateDeclaredItemCustomFields_AcceptsValuesMatchingTheirTypes()
    {
        var fields = new Dictionary<string, JsonElement>
        {
            ["steward"] = JsonSerializer.SerializeToElement("Planning"),
            ["priority"] = JsonSerializer.SerializeToElement(2),
            ["published"] = JsonSerializer.SerializeToElement(true),
            ["reviewed_on"] = JsonSerializer.SerializeToElement("2026-11-21"),
            ["programs"] = JsonSerializer.SerializeToElement<string[]>(["stormwater", "roads"]),
        };
        var errors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateDeclaredItemCustomFields(
            errors, new("ds-1", MetadataV2CustomFieldKinds.Dataset, fields), ItemSchemas);
        errors.Should().BeEmpty();
    }

    [UnitTest]
    public void ValidateDeclaredItemCustomFields_RejectsWrongTypesAndUndeclaredKeys()
    {
        var fields = new Dictionary<string, JsonElement>
        {
            ["steward"] = JsonSerializer.SerializeToElement("UnknownDept"),
            ["priority"] = JsonSerializer.SerializeToElement("not-a-number"),
            ["published"] = JsonSerializer.SerializeToElement("yes"),
            ["reviewed_on"] = JsonSerializer.SerializeToElement("not-a-date"),
            ["programs"] = JsonSerializer.SerializeToElement(123),
            ["extra_field"] = JsonSerializer.SerializeToElement("nope"),
        };
        var owner = new MetadataV2CustomFieldOwner("ds-1", MetadataV2CustomFieldKinds.Dataset, fields);

        var strictErrors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateDeclaredItemCustomFields(strictErrors, owner, ItemSchemas);
        strictErrors.Should().HaveCount(6).And.Contain(e => e.Contains("extra_field is not declared", StringComparison.Ordinal));

        var lenientErrors = new List<string>();
        MetadataV2CustomFieldValidation.ValidateItemCustomFields(lenientErrors, owner, ItemSchemas);
        lenientErrors.Should().HaveCount(5).And.NotContain(e => e.Contains("extra_field", StringComparison.Ordinal));
    }

    private static DescriptiveMetadata ParcelsDescriptive()
    {
        var graph = new MetadataV2Graph
        {
            Environment = "prod",
            GeneratedAt = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero),
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "ds-parcels",
                        Name = "parcels",
                        Title = "County Parcels",
                        CustomFields = new Dictionary<string, JsonElement>
                        {
                            ["steward"] = JsonSerializer.SerializeToElement("Planning"),
                            ["tier"] = JsonSerializer.SerializeToElement(1),
                        },
                    },
                },
            ],
        };
        return DescriptiveMetadataResolver.ForDataset(graph, new(graph.Resources[0], null));
    }

    [UnitTest]
    public void CrosswalkWriters_EmitCustomFieldsInExtensionOutput()
    {
        var descriptive = ParcelsDescriptive();
        descriptive.CustomFields["steward"].GetString().Should().Be("Planning");

        var dcat = DcatApWriter.Write(descriptive, "https://example.org/records/ds-parcels");
        dcat["@context"]!.AsObject()["honua"]!.GetValue<string>().Should().Be("https://honua.io/ns/metadata#");
        dcat["honua:customFields"]!.AsObject()["steward"]!.GetValue<string>().Should().Be("Planning");
        dcat["honua:customFields"]!.AsObject()["tier"]!.GetValue<int>().Should().Be(1);

        const string expected = """honua:customFields={"steward":"Planning","tier":1}""";
        Iso19115Part3Writer.Write(descriptive).Descendants().Single(e => e.Name.LocalName == "supplementalInformation").Value
            .Should().Be(expected);
        Iso19139Writer.Write(descriptive).Descendants().Single(e => e.Name.LocalName == "supplementalInformation").Value
            .Should().Be(expected);
    }

    [UnitTest]
    public void CrosswalkWriters_OmitExtensionOutput_WhenCustomFieldsAreEmpty()
    {
        var empty = ParcelsDescriptive() with { CustomFields = new Dictionary<string, JsonElement>() };

        DcatApWriter.Write(empty, "https://example.org/records/ds-parcels").ContainsKey("honua:customFields").Should().BeFalse();
        Iso19115Part3Writer.Write(empty).Descendants().Where(e => e.Name.LocalName == "supplementalInformation").Should().BeEmpty();
        Iso19139Writer.Write(empty).Descendants().Where(e => e.Name.LocalName == "supplementalInformation").Should().BeEmpty();
    }
}
