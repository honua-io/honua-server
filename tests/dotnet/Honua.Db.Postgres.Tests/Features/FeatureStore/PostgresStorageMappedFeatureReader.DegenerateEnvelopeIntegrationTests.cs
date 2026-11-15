// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// A bbox whose corners coincide (an OGC <c>bbox=x,y,x,y</c>) or that collapses to a line must
/// still select the features it touches on a source-backed layer. ST_MakeEnvelope builds a
/// zero-area polygon for these boxes, and GEOS reports that polygon as intersecting nothing, so a
/// point exactly at the box was dropped while the canonical store (NTS geometry) returned it.
/// </summary>
public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    // Honolulu is stored at exactly (-157.8583, 21.3069); Kahului at (-156.3319, 20.7984).
    [IntegrationTheory]
    [InlineData(-157.8583, 21.3069, -157.8583, 21.3069, new long[] { 1 }, false)]
    [InlineData(-157.8583, 21.3069, -157.8583, 21.3069, new long[] { 1 }, true)]
    [InlineData(-157.8583, 21.0, -157.8583, 21.5, new long[] { 1 }, false)]
    [InlineData(-158.0, 21.3069, -157.0, 21.3069, new long[] { 1 }, false)]
    [InlineData(-157.0, 21.0, -157.0, 21.0, new long[0], false)]
    [InlineData(-158.0, 20.0, -156.0, 22.0, new long[] { 1, 2 }, false)]
    public async Task QueryPageAndCount_DegenerateEnvelope_SelectsCoincidentPoints(
        double minX, double minY, double maxX, double maxY, long[] expectedIds, bool preferSerialPlan)
    {
        var reader = CreateReader(preferSerialPlan: preferSerialPlan);
        var query = new FeatureQuery
        {
            Limit = 100,
            SpatialFilter = EnvelopeFilter(minX, minY, maxX, maxY),
        };

        var page = await reader.QueryPageAsync(1, query);
        var count = await reader.CountAsync(1, query);

        page.Items.Select(feature => feature.Id).Should().Equal(expectedIds,
            "an inclusive bbox selects every point on or inside it, including a box collapsed to a point or line");
        count.Should().Be(expectedIds.Length);
    }

    private static SpatialFilter EnvelopeFilter(double minX, double minY, double maxX, double maxY)
    {
        // Mirrors the OGC adapter: NTS turns a collapsed envelope into a Point/LineString,
        // while the explicit envelope bounds drive the storage-mapped SQL.
        var geometry = new GeometryFactory(new PrecisionModel(), 4326)
            .ToGeometry(new Envelope(minX, maxX, minY, maxY));
        return new SpatialFilter
        {
            Geometry = new WKBWriter().Write(geometry),
            Srid = 4326,
            SpatialRelationship = SpatialRelationship.Intersects,
            IsSimpleEnvelope = true,
            AllowEnvelopeOnly = true,
            EnvelopeMinX = minX,
            EnvelopeMinY = minY,
            EnvelopeMaxX = maxX,
            EnvelopeMaxY = maxY,
        };
    }
}
