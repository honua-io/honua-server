// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Edit;

/// <summary>
/// Unit coverage for <see cref="ContingentValueValidator"/> (#2133): the shared edit-path
/// enforcement of a resource's contingent-value field groups. Verifies that a valid cross-field
/// combination is accepted, an invalid one is rejected naming the offending group, the
/// <c>any</c> wildcard and <c>null</c>/coded/range contingency types behave per Esri semantics,
/// and a partial update validates the full effective (merged) row rather than only changed fields.
/// </summary>
public sealed class ContingentValueValidatorTests
{
    [UnitTest]
    public void Validate_ValidCombination_IsAccepted()
    {
        var resource = ResourceWithGroup(Group(
            Combination(1, ("color", Code("red")), ("size", Code("S"))),
            Combination(2, ("color", Code("blue")), ("size", Code("L")))));

        var result = ContingentValueValidator.Validate(
            resource,
            Attributes(("color", "red"), ("size", "S")));

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_InvalidCombination_IsRejectedNamingGroup()
    {
        var resource = ResourceWithGroup(Group(
            Combination(1, ("color", Code("red")), ("size", Code("S")))));

        var result = ContingentValueValidator.Validate(
            resource,
            Attributes(("color", "red"), ("size", "L")));

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainSingle()
            .Which.FieldGroupName.Should().Be("colorSize");
    }

    [UnitTest]
    public void Validate_AnyWildcard_AcceptsAnyValueForField()
    {
        var resource = ResourceWithGroup(Group(
            Combination(1, ("color", Code("blue")), ("size", Any()))));

        var result = ContingentValueValidator.Validate(
            resource,
            Attributes(("color", "blue"), ("size", "anything-goes")));

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_RangeContingency_HonorsBounds()
    {
        var resource = ResourceWithGroup(Group(
            Combination(1, ("color", Code("red")), ("weight", Range(0, 10)))));

        ContingentValueValidator.Validate(resource, Attributes(("color", "red"), ("weight", 5)))
            .IsValid.Should().BeTrue();
        ContingentValueValidator.Validate(resource, Attributes(("color", "red"), ("weight", 25)))
            .IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void Validate_NullContingency_RequiresNull()
    {
        var resource = ResourceWithGroup(Group(
            Combination(1, ("color", Code("none")), ("size", Null()))));

        ContingentValueValidator.Validate(resource, Attributes(("color", "none"), ("size", null)))
            .IsValid.Should().BeTrue();
        ContingentValueValidator.Validate(resource, Attributes(("color", "none"), ("size", "S")))
            .IsValid.Should().BeFalse();
    }

    [UnitTest]
    public void Validate_PartialUpdate_ValidatesMergedRowNotJustChangedField()
    {
        var resource = ResourceWithGroup(Group(
            Combination(1, ("color", Code("red")), ("size", Code("S")))));

        // Changing only "size" without the existing "color" present would never match the
        // combination (color resolves to null). The effective merged row must be validated.
        var changedOnly = ContingentValueValidator.Validate(resource, Attributes(("size", "S")));
        changedOnly.IsValid.Should().BeFalse();

        var merged = ContingentValueValidator.Validate(resource, Attributes(("color", "red"), ("size", "S")));
        merged.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_NonRestrictiveGroup_NeverRejects()
    {
        var group = Group(Combination(1, ("color", Code("red")), ("size", Code("S")))) with { Restrictive = false };
        var resource = ResourceWithGroup(group);

        ContingentValueValidator.Validate(resource, Attributes(("color", "red"), ("size", "L")))
            .IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_FieldsNotInAnyGroup_AreUnaffected()
    {
        var resource = ResourceWithGroup(Group(
            Combination(1, ("color", Code("red")), ("size", Code("S")))));

        ContingentValueValidator.Validate(
            resource,
            Attributes(("color", "red"), ("size", "S"), ("notes", "free text")))
            .IsValid.Should().BeTrue();
    }

    private static MetadataV2Resource ResourceWithGroup(MetadataV2ContingentValueGroup group)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res", Name = "Test" },
            ContingentValueGroups = [group]
        };

    private static MetadataV2ContingentValueGroup Group(params MetadataV2ContingentValue[] combinations)
        => new()
        {
            Name = "colorSize",
            Restrictive = true,
            Fields = ["color", "size", "weight"],
            ContingentValues = combinations
        };

    private static MetadataV2ContingentValue Combination(
        int id,
        params (string Field, MetadataV2ContingentFieldValue Value)[] values)
    {
        var dict = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in values)
        {
            dict[field] = value;
        }

        return new MetadataV2ContingentValue { Id = id, Values = dict };
    }

    private static MetadataV2ContingentFieldValue Code(string value)
        => new() { Type = "code", Code = JsonElementOf($"\"{value}\"") };

    private static MetadataV2ContingentFieldValue Any() => new() { Type = "any" };

    private static MetadataV2ContingentFieldValue Null() => new() { Type = "null" };

    private static MetadataV2ContingentFieldValue Range(double min, double max)
        => new() { Type = "range", Range = [JsonElementOf(min.ToString(System.Globalization.CultureInfo.InvariantCulture)), JsonElementOf(max.ToString(System.Globalization.CultureInfo.InvariantCulture))] };

    private static JsonElement JsonElementOf(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, object?> Attributes(params (string Key, object? Value)[] pairs)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }
}
