// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

// A bbox whose corners coincide (an OGC bbox=x,y,x,y) or that collapses to a line must still
// select the features it touches on a source-backed layer. ST_MakeEnvelope builds a zero-area
// polygon for these boxes, and GEOS reports that polygon as intersecting nothing, so a point
// exactly at the box was dropped while the canonical store (NTS geometry) returned it.
public sealed partial class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests
{
    // Honolulu (id 1) is stored at exactly (-157.8583, 21.3069); Kahului (id 2) at (-156.3319, 20.7984).
    private static readonly Envelope HonoluluPoint = new(-157.8583, -157.8583, 21.3069, 21.3069);

    // Each box is minX, minY, maxX, maxY, as in an OGC bbox parameter.
    public static TheoryData<double[], long[]> DegenerateEnvelopeCases => new()
    {
        { [-157.8583, 21.3069, -157.8583, 21.3069], [1] },
        { [-157.8583, 21.0, -157.8583, 21.5], [1] },
        { [-158.0, 21.3069, -157.0, 21.3069], [1] },
        { [-157.0, 21.0, -157.0, 21.0], [] },
        { [-158.0, 20.0, -156.0, 22.0], [1, 2] },
    };

    [IntegrationTheory]
    [MemberData(nameof(DegenerateEnvelopeCases))]
    public async Task QueryPageAndCount_DegenerateEnvelope_SelectsCoincidentPoints(double[] bbox, long[] expectedIds)
    {
        var box = new Envelope(bbox[0], bbox[2], bbox[1], bbox[3]);

        await AssertEnvelopeSelectsAsync(CreateReader(), box, expectedIds);
    }

    [IntegrationTest]
    public async Task QueryPageAndCount_PointEnvelopeOnSerialBoundedPlan_SelectsCoincidentPoint()
    {
        // The bounded serial plan only engages for source-backed point layers; the smallint hint
        // is the fixture's source-backed mapping and does not affect a spatial-only query.
        var reader = CreateReader(smallintHint: true, preferSerialPlan: true);

        await AssertEnvelopeSelectsAsync(reader, HonoluluPoint, [1]);
    }

    private static async Task AssertEnvelopeSelectsAsync(
        PostgresStorageMappedFeatureReader reader, Envelope box, long[] expectedIds)
    {
        var query = new FeatureQuery { Limit = 100, SpatialFilter = EnvelopeFilter(box) };

        var page = await reader.QueryPageAsync(1, query);
        var count = await reader.CountAsync(1, query);

        page.Items.Select(feature => feature.Id).Should().Equal(expectedIds,
            "an inclusive bbox selects every point on or inside it, including a box collapsed to a point or line");
        count.Should().Be(expectedIds.Length);
    }

    private static SpatialFilter EnvelopeFilter(Envelope box)
    {
        // Mirrors the OGC adapter: NTS turns a collapsed envelope into a Point/LineString,
        // while the explicit envelope bounds drive the storage-mapped SQL.
        var geometry = new GeometryFactory(new PrecisionModel(), 4326).ToGeometry(box);
        return new SpatialFilter
        {
            Geometry = new WKBWriter().Write(geometry),
            Srid = 4326,
            SpatialRelationship = SpatialRelationship.Intersects,
            IsSimpleEnvelope = true,
            AllowEnvelopeOnly = true,
            EnvelopeMinX = box.MinX,
            EnvelopeMinY = box.MinY,
            EnvelopeMaxX = box.MaxX,
            EnvelopeMaxY = box.MaxY,
        };
    }
}
