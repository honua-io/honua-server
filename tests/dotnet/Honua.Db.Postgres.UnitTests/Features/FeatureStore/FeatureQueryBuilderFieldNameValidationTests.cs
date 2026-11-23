// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class FeatureQueryBuilderFieldNameValidationTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("_private")]
    [InlineData("Field_123")]
    public void IsValidFieldName_WithBareIdentifier_ReturnsTrue(string fieldName)
    {
        FeatureQueryBuilder.IsValidFieldName(fieldName).Should().BeTrue();
    }

    [Theory]
    [InlineData("eo:cloud_cover")]
    [InlineData("sar:polarizations")]
    [InlineData("some-field")]
    [InlineData("some.field")]
    [InlineData("1st_field")]
    public void IsValidFieldName_WithNonIdentifierShapedName_ReturnsFalse(string fieldName)
    {
        // These are legitimate jsonb keys, but they are NOT safe to interpolate as a
        // bare SQL identifier, so the identifier-oriented validator must keep saying no.
        FeatureQueryBuilder.IsValidFieldName(fieldName).Should().BeFalse();
    }

    [Theory]
    [InlineData("eo:cloud_cover")]
    [InlineData("sar:polarizations")]
    [InlineData("proj:epsg")]
    [InlineData("view:sun_azimuth")]
    [InlineData("some-field")]
    [InlineData("some.field")]
    [InlineData("name")]
    [InlineData("1st_field")]
    [InlineData("名前")]
    [InlineData("länge_straße")]
    [InlineData("ʻāina_inoa")]
    public void IsValidJsonAttributeKey_WithDeclaredExtensionPropertyName_ReturnsTrue(string fieldName)
    {
        FeatureQueryBuilder.IsValidJsonAttributeKey(fieldName).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name'); DROP TABLE features; --")]
    [InlineData("name'; DELETE FROM features; --")]
    [InlineData("name' || (SELECT 1) || '")]
    [InlineData("name\" , (SELECT 1) AS \"x")]
    [InlineData("field name")]
    [InlineData("field\tname")]
    [InlineData("field\nname")]
    [InlineData("field\0name")]
    [InlineData("attributes->>'name'")]
    [InlineData("count(*)")]
    [InlineData("$1")]
    [InlineData("名'前")]
    [InlineData("名 前")]
    public void IsValidJsonAttributeKey_WithInjectionShapedInput_ReturnsFalse(string fieldName)
    {
        // The key is parameter-bound at every call site, so this allow-list is defense
        // in depth rather than the thing that makes the SQL safe — but it must still
        // reject anything that is not plausibly a field name.
        FeatureQueryBuilder.IsValidJsonAttributeKey(fieldName).Should().BeFalse();
    }

    [Fact]
    public void IsValidJsonAttributeKey_WithLongDeclaredName_ReturnsTrue()
    {
        // JSON-backed field names are parameter-bound at every call site and the
        // metadata contract does not declare a 255-character limit. Do not impose an
        // identifier-style length cap that makes an advertised field unqueryable.
        FeatureQueryBuilder.IsValidJsonAttributeKey(new string('a', 1_024)).Should().BeTrue();
    }

    [Fact]
    public void IsValidEncodedColumnAlias_EnforcesPostgresIdentifierByteLimit()
    {
        FeatureQueryBuilder.IsValidEncodedColumnAlias(new string('a', 63)).Should().BeTrue();
        FeatureQueryBuilder.IsValidEncodedColumnAlias(new string('a', 64)).Should().BeFalse();

        FeatureQueryBuilder.IsValidJsonAttributeKey(new string('a', 64)).Should().BeTrue();
    }

    [Theory]
    [InlineData("件数")]
    [InlineData("count_all")]
    public void IsValidOutputAlias_WithUnicodeIdentifier_ReturnsTrue(string alias)
        => FeatureQueryBuilder.IsValidOutputAlias(alias).Should().BeTrue();

    [Theory]
    [InlineData("件 数")]
    [InlineData("count\"; --")]
    public void IsValidOutputAlias_WithQuoteOrSpace_ReturnsFalse(string alias)
        => FeatureQueryBuilder.IsValidOutputAlias(alias).Should().BeFalse();

    [Fact]
    public void IsValidEncodedColumnAlias_AcceptsSupplementaryPlaneLetters()
    {
        // U+20BB7 is one letter stored as a surrogate pair; a column the storage-mapped
        // reader projects must also be representable as a binary projection alias.
        const string name = "\U00020BB7_name";
        SqlIdentifier.IsUnicodeIdentifier(name).Should().BeTrue();
        FeatureQueryBuilder.IsValidJsonAttributeKey(name).Should().BeTrue();
        FeatureQueryBuilder.IsValidEncodedColumnAlias(name).Should().BeTrue();
        FeatureQueryBuilder.IsValidJsonAttributeKey("a\uD842").Should().BeFalse();
    }

    [Fact]
    public void IsValidJsonAttributeKey_IsStrictlyWiderThanIsValidFieldName()
    {
        // Anything the identifier validator accepts must remain acceptable as a jsonb
        // key; the reverse must not hold, or the split has collapsed back into one
        // validator and the identifier call sites have been silently widened.
        string[] identifierNames = ["name", "_private", "Field_123", "objectid"];
        foreach (var fieldName in identifierNames)
        {
            FeatureQueryBuilder.IsValidFieldName(fieldName).Should().BeTrue();
            FeatureQueryBuilder.IsValidJsonAttributeKey(fieldName).Should().BeTrue();
        }

        FeatureQueryBuilder.IsValidJsonAttributeKey("eo:cloud_cover").Should().BeTrue();
        FeatureQueryBuilder.IsValidFieldName("eo:cloud_cover").Should().BeFalse();
    }
}
