// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Services;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Issue #4600, acceptance criterion 4: an imported domain is verified by behavior, not only by JSON
/// capture. Each Esri field definition is parsed by <see cref="EsriFieldDomainParser"/>, the parser the
/// importer uses to build the published field (persistence through publish is proven separately by
/// <c>GeoservicesImportDomainPersistenceTests</c>). The resulting field is then run through the
/// <see cref="FeatureMutationValidator"/> the FeatureServer <c>applyEdits</c> handler uses, in
/// GeoServices mode.
/// </summary>
/// <remarks>
/// Expected outcomes come from the source domain definitions in this file, not from validator output.
/// The coded-value domain allows exactly A, C and P; the range domain allows 0 through 100 inclusive.
/// This is the behavior the capability registry now claims for <c>resource.domains</c>. The truncated
/// case pins its fallback: a coded-value domain over the capture cap is not persisted, so it is not
/// enforced either.
/// </remarks>
[Protocol(TestProtocols.FeatureServer)]
public sealed class ImportedDomainEnforcementTests
{
    private const string CodedValueField = """
        {
          "name": "status",
          "type": "esriFieldTypeString",
          "domain": {
            "type": "codedValue",
            "name": "StatusDomain",
            "codedValues": [
              { "code": "A", "name": "Active" },
              { "code": "C", "name": "Closed" },
              { "code": "P", "name": "Pending" }
            ]
          }
        }
        """;

    private const string RangeField = """
        {
          "name": "score",
          "type": "esriFieldTypeInteger",
          "domain": { "type": "range", "name": "ScoreRange", "range": [0, 100] }
        }
        """;

    [Theory]
    [Operation(Operations.ApplyEdits)]
    [InlineData("A")]
    [InlineData("C")]
    [InlineData("P")]
    public void ApplyEditsValidation_ImportedCodedValueDomain_AcceptsEverySourceCode(string code)
    {
        var result = Validate(ParseField(CodedValueField, MetadataV2FieldType.String), "status", code);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
    }

    [Theory]
    [Operation(Operations.ApplyEdits)]
    [InlineData("X")]
    [InlineData("Active")]
    [InlineData("a")]
    public void ApplyEditsValidation_ImportedCodedValueDomain_RejectsValuesOutsideTheSourceDomain(string value)
    {
        var result = Validate(ParseField(CodedValueField, MetadataV2FieldType.String), "status", value);

        result.IsValid.Should().BeFalse($"'{value}' is not one of the source domain codes A, C, P");
        result.ErrorMessage.Should().Contain("status").And.Contain("coded-value domain");
    }

    [Theory]
    [Operation(Operations.ApplyEdits)]
    [InlineData(0L, true)]
    [InlineData(57L, true)]
    [InlineData(100L, true)]
    [InlineData(-1L, false)]
    [InlineData(101L, false)]
    public void ApplyEditsValidation_ImportedRangeDomain_EnforcesTheSourceBoundsInclusively(long value, bool expectedValid)
    {
        var result = Validate(ParseField(RangeField, MetadataV2FieldType.Integer), "score", value);

        result.IsValid.Should().Be(expectedValid, "the source range domain is [0, 100] inclusive");
        if (!expectedValid)
        {
            result.ErrorMessage.Should().Contain("outside the allowed range [0, 100]");
        }
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void ApplyEditsValidation_CodedValueDomainOverTheCaptureCap_IsNotPersistedAndNotEnforced()
    {
        var codes = Enumerable.Range(0, EsriFieldDomainParser.CodedValueDomainCap + 1)
            .Select(static i => $$"""{ "code": "C{{i}}", "name": "Code {{i}}" }""");
        var fieldJson = $$"""
            {
              "name": "zone",
              "type": "esriFieldTypeString",
              "domain": { "type": "codedValue", "name": "ZoneDomain", "codedValues": [{{string.Join(",", codes)}}] }
            }
            """;

        using var document = JsonDocument.Parse(fieldJson);
        var parsed = EsriFieldDomainParser.Parse(document.RootElement);
        parsed.Truncated.Should().BeTrue();
        parsed.Domain.Should().BeNull("a truncated coded-value domain is not persisted");

        var field = new MetadataV2Field { Name = "zone", Type = MetadataV2FieldType.String, Nullable = true, Domain = parsed.Domain };
        Validate(field, "zone", "not-a-source-code").IsValid.Should().BeTrue(
            "with no persisted domain nothing is enforced, which is why the registry reports a truncated domain as manual review");
    }

    private static MetadataV2Field ParseField(string fieldJson, MetadataV2FieldType type)
    {
        using var document = JsonDocument.Parse(fieldJson);
        var root = document.RootElement;
        var parsed = EsriFieldDomainParser.Parse(root);
        parsed.Truncated.Should().BeFalse();
        parsed.Domain.Should().NotBeNull();

        return new MetadataV2Field
        {
            Name = root.GetProperty("name").GetString()!,
            Type = type,
            Nullable = true,
            Domain = parsed.Domain
        };
    }

    private static Honua.Core.Features.Validation.ValidationResult<System.Collections.Immutable.ImmutableDictionary<string, object?>> Validate(
        MetadataV2Field field,
        string attribute,
        object value)
    {
        var resource = new MetadataV2Resource { SchemaFields = [field] };
        var validator = new FeatureMutationValidator(Substitute.For<IGeometryValidator>());

        return validator.ValidateAttributes(
            resource,
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [attribute] = value },
            ValidationExtensions.AttributeValidationMode.GeoServices);
    }
}
