// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.GeoJson;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class GeoJsonFeatureBaseBuilderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Create_PreservesVisibilityProjectionDatesAndIdentifiers(bool additional, bool encoded, bool prepared)
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.Integer, SemanticRoles = ["id.primary"] },
                new() { Name = "name", Type = MetadataV2FieldType.String },
                new() { Name = "date", Type = MetadataV2FieldType.Date },
                new() { Name = "timestamp", Type = MetadataV2FieldType.DateTime },
                new() { Name = "nullable", Type = MetadataV2FieldType.String },
                new() { Name = "nested", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String, Hidden = true },
                new() { Name = "unselected", Type = MetadataV2FieldType.String },
                new() { Name = "shape", Type = MetadataV2FieldType.Geometry },
            ]
        };
        var nested = new Dictionary<string, object?> { ["value"] = 42L };
        var attributes = new Dictionary<string, object?>
        {
            ["name"] = "park",
            ["NAME"] = "must not duplicate",
            ["date"] = 0L,
            ["timestamp"] = new DateTimeOffset(1970, 1, 1, 2, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_567),
            ["nullable"] = null,
            ["nested"] = nested,
            ["SECRET"] = "private",
            ["unselected"] = "omit",
            ["extra"] = "additional"
        }.ToImmutableDictionary();
        var options = new GeoJsonFeatureBuildOptions(
            ProjectedProperties: new HashSet<string>(StringComparer.Ordinal)
            { "name", "date", "timestamp", "nullable", "nested", "SECRET", "extra" },
            IncludeObjectIdProperty: true, IncludeObjectIdAlias: true,
            IncludeAdditionalAttributes: additional, ResolveIdFromProperties: true);

        if (prepared)
        {
            options = GeoJsonFeatureBaseBuilder.PrepareOptions(resource, options);
        }

        var result = encoded
            ? GeoJsonFeatureBaseBuilder.Create(EncodedGeoJsonFeature.Create(7, null, attributes), resource, options)
            : GeoJsonFeatureBaseBuilder.Create(Feature.Create(7, null, attributes), resource, options);

        result.Id.Should().Be(7L);
        result.HasGeometry.Should().BeFalse();
        result.Properties.Should().Contain("name", "park");
        result.Properties.Should().Contain("date", "1970-01-01");
        result.Properties.Should().Contain("timestamp", "1970-01-01T00:00:00.1234567Z");
        result.Properties.Should().Contain("objectid", 7L).And.Contain("OBJECTID", 7L);
        result.Properties["nullable"].Should().BeNull();
        result.Properties["nested"].Should().BeSameAs(nested);
        result.Properties.Keys.Should().NotContain(["secret", "SECRET", "NAME", "unselected", "shape"]);
        result.Properties.ContainsKey("extra").Should().Be(additional);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_PreparedPageAvoidsRepeatedSchemaAllocation(bool additional)
    {
        var fields = Enumerable.Range(0, 16)
            .Select(index => new MetadataV2Field { Name = $"field{index}", Type = MetadataV2FieldType.String })
            .ToArray();
        var resource = new MetadataV2Resource { SchemaFields = fields };
        var feature = Feature.Create(1, null,
            fields.ToImmutableDictionary(field => field.Name, _ => (object?)"value"));
        var options = new GeoJsonFeatureBuildOptions(IncludeAdditionalAttributes: additional);

        long Measure(bool prepared)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var responseOptions = prepared ? GeoJsonFeatureBaseBuilder.PrepareOptions(resource, options) : options;
            for (var index = 0; index < 100; index++)
            {
                var result = GeoJsonFeatureBaseBuilder.Create(feature, resource, responseOptions);
                GC.KeepAlive(result.Properties);
            }
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }

        _ = Measure(false);
        _ = Measure(true);
        var ordinary = Measure(false);
        var prepared = Measure(true);
        output.WriteLine($"100-feature page bytes: ordinary={ordinary}; prepared={prepared}; additional={additional}");
        prepared.Should().BeLessThan((long)(ordinary * 0.85),
            "a response should prepare its field metadata once, including preparation in the measured allocation");
        if (!additional)
        {
            prepared.Should().BeLessThan(80_000,
                "a prepared page should avoid growing each feature's property dictionary repeatedly");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_PreparedWideSchemaBoundsAllocationToReturnedProperties(bool projected)
    {
        var fields = Enumerable.Range(0, 512)
            .Select(index => new MetadataV2Field { Name = $"field{index}", Type = MetadataV2FieldType.String })
            .ToArray();
        var resource = new MetadataV2Resource { SchemaFields = fields };
        var attributes = (projected ? fields : fields.Take(1))
            .ToImmutableDictionary(field => field.Name, _ => (object?)"value");
        var feature = Feature.Create(1, null, attributes);
        var options = new GeoJsonFeatureBuildOptions(
            ProjectedProperties: projected ? new HashSet<string> { "field0" } : null);

        long Measure()
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var prepared = GeoJsonFeatureBaseBuilder.PrepareOptions(resource, options);
            for (var index = 0; index < 100; index++)
            {
                var result = GeoJsonFeatureBaseBuilder.Create(feature, resource, prepared);
                GC.KeepAlive(result.Properties);
            }
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }

        var result = GeoJsonFeatureBaseBuilder.Create(feature, resource,
            GeoJsonFeatureBaseBuilder.PrepareOptions(resource, options));
        result.Properties.Should().HaveCount(1).And.Contain("field0", "value");
        _ = Measure();
        var allocated = Measure();
        output.WriteLine($"100-feature wide-schema page bytes: {allocated}; projected={projected}");
        allocated.Should().BeLessThan(100_000,
            "a sparse or projected response must not reserve the full schema size per feature");
    }

    [Fact]
    public void Create_PreparedOptionsRejectDifferentResource()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields = [new() { Name = "secret", Type = MetadataV2FieldType.String }]
        };
        var restricted = resource with
        {
            SchemaFields = [new() { Name = "secret", Type = MetadataV2FieldType.String, Hidden = true }]
        };
        var options = GeoJsonFeatureBaseBuilder.PrepareOptions(resource);
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("secret", "private"));
        var call = () => GeoJsonFeatureBaseBuilder.Create(feature, restricted, options);
        call.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_PreparedOptionsRejectDifferentAttributePolicy()
    {
        var resource = new MetadataV2Resource { SchemaFields = [] };
        var options = GeoJsonFeatureBaseBuilder.PrepareOptions(resource);
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("extra", "value"));
        var call = () => GeoJsonFeatureBaseBuilder.Create(feature, resource,
            options with { IncludeAdditionalAttributes = true });
        call.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_PreparedSchemaKeepsProjectionAndHiddenFieldRules()
    {
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new() { Name = "name", Type = MetadataV2FieldType.String },
                new() { Name = "status", Type = MetadataV2FieldType.String },
                new() { Name = "secret", Type = MetadataV2FieldType.String, Hidden = true }
            ]
        };
        var feature = Feature.Create(1, null, new Dictionary<string, object?>
        {
            ["name"] = "park",
            ["status"] = "open",
            ["secret"] = "private"
        }.ToImmutableDictionary());
        var options = GeoJsonFeatureBaseBuilder.PrepareOptions(resource,
            new GeoJsonFeatureBuildOptions(
                ProjectedProperties: new HashSet<string> { "name" }, IncludeAdditionalAttributes: true));
        var first = GeoJsonFeatureBaseBuilder.Create(feature, resource, options);
        var second = GeoJsonFeatureBaseBuilder.Create(feature, resource,
            options with { ProjectedProperties = new HashSet<string> { "status", "secret" } });
        first.Properties.Should().HaveCount(1).And.Contain("name", "park");
        second.Properties.Should().HaveCount(1).And.Contain("status", "open");
    }

    [Fact]
    public void Create_DeclaredOnlyPageStaysWithinAllocationBudget()
    {
        var fields = Enumerable.Range(0, 16)
            .Select(index => new MetadataV2Field { Name = $"field{index}", Type = MetadataV2FieldType.String })
            .ToArray();
        var resource = new MetadataV2Resource { SchemaFields = fields };
        var feature = Feature.Create(1, null,
            fields.ToImmutableDictionary(field => field.Name, _ => (object?)"value"));

        long MeasurePage(bool additional)
        {
            var options = new GeoJsonFeatureBuildOptions(IncludeAdditionalAttributes: additional);
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 100; index++)
            {
                var result = GeoJsonFeatureBaseBuilder.Create(feature, resource, options);
                GC.KeepAlive(result.Properties);
            }

            return GC.GetAllocatedBytesForCurrentThread() - start;
        }

        // Warm both paths before measuring allocations on this thread. Both pages
        // contain exactly the same declared attributes; no wall-clock threshold.
        _ = MeasurePage(false);
        _ = MeasurePage(true);
        var declared = MeasurePage(false);
        var additional = MeasurePage(true);
        output.WriteLine($"100-feature page bytes: declared={declared}; additional={additional}");
        // The unchanged builder allocates 284,000 bytes for this page. Keep
        // enough headroom for runtime variation while rejecting that baseline.
        declared.Should().BeLessThanOrEqualTo(200_000,
            "the ordinary declared-only response should avoid optional attribute bookkeeping");
    }
}
