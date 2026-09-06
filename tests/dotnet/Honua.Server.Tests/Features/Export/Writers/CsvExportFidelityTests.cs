// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

/// <summary>
/// Pins the two CSV export behaviours that honua-server#4419 found unasserted: the exported
/// geometry cell's exact shape — which today carries <b>no CRS at all</b> — and the writer's typed
/// value formatting, where every existing test declared only <see cref="ExportFieldType.String"/>
/// so the numeric, boolean and temporal branches were never reached.
/// </summary>
public sealed class CsvExportFidelityTests
{
    /// <summary>
    /// <b>Known fidelity gap, now detected.</b> <c>CsvExportWriter</c> writes bare OGC WKT with no
    /// <c>SRID=</c> prefix and emits no sidecar, so the spatial reference the caller exported in is
    /// not recoverable from the file: a consumer re-importing it must guess, and guessing wrong is
    /// silent wrong data. Nothing asserted this, so neither the loss nor a future fix would have
    /// been noticed. This test states the current contract exactly; a change that starts carrying
    /// the SRID must update it deliberately (honua-server#4419).
    /// </summary>
    [Fact]
    public async Task WriteAsync_ProjectedGeometry_EmitsBareWktCarryingNoSrid()
    {
        // A Web Mercator point: the ordinates alone cannot imply the CRS, which is exactly why the
        // missing SRID matters. 4326 and 3857 coordinates are not distinguishable by inspection
        // once the magnitudes overlap, and a consumer that assumes degrees puts this in the Gulf
        // of Guinea.
        var factory = new GeometryFactory(new PrecisionModel(), 3857);
        var point = factory.CreatePoint(new Coordinate(-13629378.29, 4544069.28));

        var csv = await ExportAsync(
            [Row(point, ImmutableDictionary<string, object?>.Empty.Add("name", "mercator"))],
            [new ExportField("name", ExportFieldType.String, true)]);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("name,WKT");
        lines[1].Should().Be(
            "mercator,\"POINT (-13629378.29 4544069.28)\"",
            "the geometry cell is bare WKT — no SRID= prefix, no CRS column, and no sidecar file");

        csv.Should().NotContain("SRID", "recording the loss: the exported CSV carries no CRS at all");
        csv.Should().NotContain("3857");
    }

    /// <summary>
    /// The same statement one level up: a CSV exported from a projected geometry and read straight
    /// back produces the same ordinates with no spatial reference attached, so a round trip cannot
    /// restore the CRS on its own.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ThenRead_LosesTheSpatialReferenceButNotTheOrdinates()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 3857);
        var point = factory.CreatePoint(new Coordinate(-13629378.29, 4544069.28));

        var csv = await ExportAsync(
            [Row(point, ImmutableDictionary<string, object?>.Empty.Add("name", "mercator"))],
            [new ExportField("name", ExportFieldType.String, true)]);

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var readBack = new List<NetTopologySuite.Features.IFeature>();
        await foreach (var feature in Honua.Core.Features.FileImport.Services.CsvFormatReader
                           .ReadStreamingAsync(stream, CancellationToken.None))
        {
            readBack.Add(feature);
        }

        var geometry = readBack.Should().ContainSingle().Subject.Geometry;
        geometry.Coordinate.X.Should().Be(-13629378.29);
        geometry.Coordinate.Y.Should().Be(4544069.28);
        geometry.SRID.Should().NotBe(
            3857,
            "the export carried no SRID, so the reader cannot recover the source CRS — the caller " +
            "must supply it out of band (SourceSrid on import)");
    }

    /// <summary>
    /// honua-server#4419: the writer formats <see cref="DateTime"/>, <see cref="DateTimeOffset"/>,
    /// <see cref="double"/>, <see cref="float"/>, <see cref="decimal"/> and <see cref="bool"/>
    /// through dedicated invariant-culture branches that no test reached, because every CSV export
    /// fixture declared only string fields. A locale-sensitive or lossy formatter would have gone
    /// unnoticed.
    /// </summary>
    [Fact]
    public async Task WriteAsync_TypedValues_AreFormattedWithInvariantCulture()
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("when", new DateTime(2026, 1, 15, 8, 30, 45, DateTimeKind.Utc))
            .Add("offset", new DateTimeOffset(2026, 1, 15, 8, 30, 45, TimeSpan.FromHours(-8)))
            .Add("ratio", -1234.5678d)
            .Add("small", 1.5f)
            .Add("money", 1234.56m)
            .Add("flag", false)
            .Add("missing", null);

        var csv = await ExportAsync(
            [Row(factory.CreatePoint(new Coordinate(1, 2)), attributes)],
            [
                new ExportField("when", ExportFieldType.DateTime, true),
                new ExportField("offset", ExportFieldType.DateTime, true),
                new ExportField("ratio", ExportFieldType.Double, true),
                new ExportField("small", ExportFieldType.Float, true),
                new ExportField("money", ExportFieldType.Double, true),
                new ExportField("flag", ExportFieldType.Boolean, true),
                new ExportField("missing", ExportFieldType.String, true)
            ]);

        var row = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1];
        var cells = SplitCsvRow(row);

        cells[0].Should().Be("2026-01-15T08:30:45.0000000Z", "DateTime uses the round-trip \"O\" format");
        cells[1].Should().Be("2026-01-15T08:30:45.0000000-08:00", "the offset must survive");
        cells[2].Should().Be(
            (-1234.5678d).ToString("G", CultureInfo.InvariantCulture),
            "numbers are formatted with the invariant culture, never the ambient one");
        cells[3].Should().Be("1.5");
        cells[4].Should().Be("1234.56");
        cells[5].Should().Be("false", "booleans are lower-case literals, not True/False");
        cells[6].Should().BeEmpty("a null attribute is an empty cell, not the text \"null\"");
    }

    private static Feature Row(Geometry geometry, ImmutableDictionary<string, object?> attributes)
        => Feature.Create(1, new WKBWriter(ByteOrder.LittleEndian, false, true, true).Write(geometry), attributes);

    private static async Task<string> ExportAsync(Feature[] rows, ExportField[] fields)
    {
        await using var output = new MemoryStream();
        (await CsvExportWriter.WriteAsync(output, ToAsyncEnumerable(rows), fields, CancellationToken.None))
            .Should().Be(rows.Length);
        return Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<Feature> ToAsyncEnumerable(Feature[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    /// <summary>Splits a CSV row on unquoted commas, dropping the trailing WKT cell.</summary>
    private static string[] SplitCsvRow(string row)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in row)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        cells.Add(current.ToString());
        return [.. cells];
    }
}
