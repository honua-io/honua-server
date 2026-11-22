// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.GeoJson;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class GeoJsonFeatureBaseBuilderObjectIdTests
{
    public static TheoryData<bool, bool, string> SourceIds
    {
        get
        {
            var cases = new TheoryData<bool, bool, string>();
            foreach (var encoded in new[] { false, true })
            foreach (var prepared in new[] { false, true })
            foreach (var sourceId in new[] { "null", "json-null", "missing", "zero", "present" })
            {
                cases.Add(encoded, prepared, sourceId);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(SourceIds))]
    public void Create_NullPrimaryIdFallsBackWithoutReplacingValidIds(bool encoded, bool prepared, string sourceId)
    {
        var resource = new MetadataV2Object
        {
            SchemaFields =
            [
                new() { Name = "id", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
                new() { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "park");
        if (sourceId != "missing")
        {
            attributes = attributes.Add("id", sourceId switch
            {
                "json-null" => JsonSerializer.SerializeToElement<object?>(null),
                "zero" => 0L,
                "present" => 123L,
                _ => null
            });
        }
        var options = new GeoJsonFeatureBuildOptions(
            IncludeObjectIdProperty: true, ResolveIdFromProperties: true);
        if (prepared)
        {
            options = GeoJsonFeatureBaseBuilder.PrepareOptions(resource, options);
        }

        var result = encoded
            ? GeoJsonFeatureBaseBuilder.Create(EncodedGeoJsonFeature.Create(42, null, attributes), resource, options)
            : GeoJsonFeatureBaseBuilder.Create(Feature.Create(42, null, attributes), resource, options);

        var expected = sourceId switch { "zero" => 0L, "present" => 123L, _ => 42L };
        result.Id.Should().Be(expected);
        result.Properties.Should().Contain("id", expected).And.Contain("name", "park");

        // Suppressing the ID property must still work when a source ID needs fallback.
        var suppressed = encoded
            ? GeoJsonFeatureBaseBuilder.Create(EncodedGeoJsonFeature.Create(42, null, attributes), resource,
                options with { IncludeObjectIdProperty = false })
            : GeoJsonFeatureBaseBuilder.Create(Feature.Create(42, null, attributes), resource,
                options with { IncludeObjectIdProperty = false });
        suppressed.Properties.Should().NotContainKey("id");
        suppressed.Id.Should().Be(42L);
    }
}
