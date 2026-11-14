// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Forms;

public sealed partial class FormPackageValidatorTests
{
    [UnitTheory]
    [InlineData("9007199254740990", "9007199254740992", "9007199254740993")]
    [InlineData("9007199254740993", "9007199254740992", "9007199254740992")]
    public async Task ValidateRecord_IntegerRange_RejectsOutsideOrInvertedBounds(string minimum, string maximum, string answer)
    {
        var result = await ValidateRangeAsync((minimum, maximum), answer, MetadataV2FieldType.BigInteger);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(issue => issue.Code).Should().Contain("fieldValueOutsideDomain");
    }

    [UnitTheory]
    [InlineData("9007199254740990", "9007199254740992", "9007199254740990")]
    [InlineData("9007199254740990", "9007199254740992", "9007199254740992")]
    public async Task ValidateRecord_IntegerRange_AcceptsInclusiveBounds(string minimum, string maximum, string answer)
    {
        var result = await ValidateRangeAsync((minimum, maximum), answer, MetadataV2FieldType.BigInteger);

        result.IsValid.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("1e-30", "2e-30", "3e-30")]
    [InlineData("2e-30", "1e-30", "1e-30")]
    public async Task ValidateRecord_SmallFloatingRange_RejectsOutsideOrInvertedBounds(string minimum, string maximum, string answer)
    {
        var result = await ValidateRangeAsync((minimum, maximum), answer, MetadataV2FieldType.Double);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(issue => issue.Code).Should().Contain("fieldValueOutsideDomain");
    }

    [UnitTheory]
    [InlineData("1e-30", "2e-30", "1e-30")]
    [InlineData("1e-30", "2e-30", "2e-30")]
    public async Task ValidateRecord_SmallFloatingRange_AcceptsInclusiveBounds(string minimum, string maximum, string answer)
    {
        var result = await ValidateRangeAsync((minimum, maximum), answer, MetadataV2FieldType.Double);

        result.IsValid.Should().BeTrue();
    }

    private static async Task<FormPackageValidationResult> ValidateRangeAsync(
        (string Minimum, string Maximum) bounds, string answer, MetadataV2FieldType type)
    {
        var resource = CreateResource(MetadataV2SpatialReference.Wgs84);
        resource = resource with
        {
            SchemaFields = resource.SchemaFields.Select(field => field.Name == "count"
                ? field with { Type = type } : field).ToArray()
        };
        var package = CreateIntegerRangePackage(bounds.Minimum, bounds.Maximum);
        package.Fields[0] = new FormFieldDefinition
        {
            FieldId = "count", Label = "Count", Type = type == MetadataV2FieldType.Double ? "number" : "integer",
            TargetField = "count", Required = true, Domain = package.Fields[0].Domain
        };
        var version = new FormPackageVersion
        {
            FormId = "inspection", Version = 1, Status = FormPackageStatus.Published, Package = package
        };

        return await CreateValidator(targetResource: resource).ValidateRecordAsync(version,
            (new Dictionary<string, JsonElement> { ["count"] = Json(answer) }, []));
    }

    private static FormPackageDocument CreateIntegerRangePackage(string minimum, string maximum) => new()
    {
        Title = "Counter inspection",
        Target = new FormTargetDefinition { ServiceId = "test", LayerId = 0 },
        Fields =
        [
            new FormFieldDefinition
            {
                FieldId = "count", Label = "Count", Type = "integer", TargetField = "count", Required = true,
                Domain = new FormFieldDomainDefinition { Type = "range", Min = Json(minimum), Max = Json(maximum) }
            }
        ]
    };
}
