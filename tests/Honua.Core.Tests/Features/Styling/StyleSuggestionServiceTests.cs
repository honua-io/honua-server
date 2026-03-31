// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Styling;

/// <summary>
/// Unit tests for the style suggestion service field selection logic.
/// </summary>
public sealed class StyleSuggestionServiceTests
{
    [Theory]
    [InlineData(GeometryType.Polygon)]
    [InlineData(GeometryType.MultiPolygon)]
    [InlineData(GeometryType.GeometryCollection)]
    public async Task SuggestAsync_PolygonLikeGeometry_PrefersNumericFieldForChoropleth(
        GeometryType geometryType)
    {
        // A numeric field should score higher than a categorical field for
        // polygon-like geometries (including GeometryCollection).
        var numericProfile = new FieldProfile
        {
            FieldName = "population",
            FieldType = "Double",
            TotalCount = 100,
            NullCount = 5,
            DistinctCount = 50,
            MinValue = 0.0,
            MaxValue = 1000.0,
            MeanValue = 500.0,
            StandardDeviation = 200.0
        };
        var categoricalProfile = new FieldProfile
        {
            FieldName = "status",
            FieldType = "String",
            TotalCount = 100,
            NullCount = 5,
            DistinctCount = 5
        };

        var stub = new StubFieldProfilingService(
            profiles: [numericProfile, categoricalProfile],
            numericValues: [10, 200, 400, 600, 800, 990]);

        var layer = new LayerDefinition(
            Id: 1,
            Name: "test",
            Description: null,
            GeometryType: geometryType,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new("objectid", FieldType.Integer),
                new("shape", FieldType.Geometry),
                new("population", FieldType.Double),
                new("status", FieldType.String)
            ]);

        var service = new StyleSuggestionService(stub, NullLogger<StyleSuggestionService>.Instance);
        var result = await service.SuggestAsync(layer);

        result.SuggestedField.Should().NotBeNull();
        result.SuggestedField!.Name.Should().Be("population",
            "polygon-like geometries should prefer numeric fields for choropleth");
    }

    [Theory]
    [InlineData(HonuaEdition.Pro)]
    [InlineData(HonuaEdition.Enterprise)]
    public async Task SuggestAsync_PropagatesEditionFromOptions(HonuaEdition edition)
    {
        var profile = new FieldProfile
        {
            FieldName = "category",
            FieldType = "String",
            TotalCount = 100,
            NullCount = 0,
            DistinctCount = 3,
            SampleValues = [new SampleValue("A", 40), new SampleValue("B", 30), new SampleValue("C", 30)]
        };

        var stub = new StubFieldProfilingService(profiles: [profile], numericValues: []);
        var layer = new LayerDefinition(
            Id: 1, Name: "test", Description: null,
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields: [new("category", FieldType.String)]);

        var service = new StyleSuggestionService(stub, NullLogger<StyleSuggestionService>.Instance);
        var result = await service.SuggestAsync(layer, new StyleSuggestionOptions { Edition = edition });

        result.Edition.Should().Be(edition);
    }

    [Fact]
    public async Task SuggestAsync_GeometryOnlyFallback_PropagatesEdition()
    {
        // Layer with no eligible fields triggers geometry-only fallback
        var stub = new StubFieldProfilingService(profiles: [], numericValues: []);
        var layer = new LayerDefinition(
            Id: 1, Name: "test", Description: null,
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields: [new("shape", FieldType.Geometry)]);

        var service = new StyleSuggestionService(stub, NullLogger<StyleSuggestionService>.Instance);
        var result = await service.SuggestAsync(layer, new StyleSuggestionOptions { Edition = HonuaEdition.Enterprise });

        result.Edition.Should().Be(HonuaEdition.Enterprise);
    }

    /// <summary>
    /// Minimal stub for <see cref="IFieldProfilingService"/> used by unit tests
    /// that do not require database access.
    /// </summary>
    private sealed class StubFieldProfilingService(
        IReadOnlyList<FieldProfile> profiles,
        double[] numericValues) : IFieldProfilingService
    {
        public Task<IReadOnlyList<FieldProfile>> ProfileFieldsAsync(
            int layerId, IReadOnlyList<FieldDefinition> fields, int sampleLimit,
            CancellationToken cancellationToken = default)
        {
            // Return only profiles whose field name matches the requested fields
            var result = profiles
                .Where(p => fields.Any(f =>
                    string.Equals(f.Name, p.FieldName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return Task.FromResult<IReadOnlyList<FieldProfile>>(result);
        }

        public Task<double[]> GetNumericValuesAsync(
            int layerId, string fieldName, int limit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(numericValues);
        }
    }
}
