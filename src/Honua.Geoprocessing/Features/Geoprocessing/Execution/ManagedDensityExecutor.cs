// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>analytics.density-managed</c> executor. A job-dispatchable, managed
/// (NetTopologySuite) density-binning counterpart to trunk's
/// <c>analytics.density</c>, which runs only synchronously through the
/// layer-scoped PostGIS <c>SpatialAnalytics</c> protocol. Like
/// <see cref="ManagedSpatialJoinExecutor"/>, this is the
/// workflow/codemod-reachable counterpart that runs against an INLINE
/// FeatureCollection so the lean dispatcher can construct it unconditionally
/// without a Postgres dependency.
///
/// Snaps every input geometry's point representative (centroid for non-point
/// inputs) onto a square or pointy-top hex grid of <c>cellSize</c> CRS units
/// and emits one feature per non-empty cell. The output geometry is the cell
/// polygon. Each cell carries:
/// <list type="bullet">
///   <item><c>COUNT</c>: the number of input features in the cell.</item>
///   <item><c>SUM_&lt;weightField&gt;</c>: when <c>weightField</c> is supplied
///   and parses as numeric, the sum of that field over the input features in
///   the cell. Non-numeric / missing values are skipped (not zero-substituted)
///   so the sum reflects what was actually observed.</item>
/// </list>
///
/// <c>cellSize</c> is treated as CRS units (the same convention as the
/// managed <c>geometry.buffer</c> executor). Geodesic conversion is not
/// performed here; callers operating on a geographic CRS must supply a
/// cellSize already in the layer's coordinate units.
/// </summary>
internal sealed class ManagedDensityExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "analytics.density-managed";
    internal const string CountAttribute = "COUNT";
    internal const string CellRowAttribute = "CELL_ROW";
    internal const string CellColumnAttribute = "CELL_COL";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var mode = ReadMode(inputs);
        var cellSize = ReadCellSize(inputs);
        var weightField = inputs.GetOrDefault("weightField", "").Trim();
        var weightOutputName = string.IsNullOrEmpty(weightField) ? null : $"SUM_{weightField}";

        var bins = new Dictionary<(int Col, int Row), BinAccumulator>();
        var binOrder = new List<(int Col, int Row)>();
        // Cell geometries reuse the first non-empty source geometry's factory so
        // the output cells inherit the input CRS / SRID (CRS-units cellSize is
        // meaningless without it). Falls back to a default factory only if the
        // input is empty, in which case there are no cells to emit anyway.
        GeometryFactory? factory = null;

        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            var representative = geometry is Point p ? p.Coordinate : geometry.Centroid.Coordinate;
            if (representative is null || double.IsNaN(representative.X) || double.IsNaN(representative.Y))
            {
                continue;
            }

            factory ??= geometry.Factory;

            var (col, row) = mode == BinMode.Hex
                ? AssignHex(representative.X, representative.Y, cellSize)
                : AssignSquare(representative.X, representative.Y, cellSize);

            if (!bins.TryGetValue((col, row), out var accumulator))
            {
                var cellGeometry = mode == BinMode.Hex
                    ? BuildHexCell(factory, col, row, cellSize)
                    : BuildSquareCell(factory, col, row, cellSize);
                accumulator = new BinAccumulator(cellGeometry, col, row);
                bins[(col, row)] = accumulator;
                binOrder.Add((col, row));
            }

            accumulator.Count++;
            if (weightField is { Length: > 0 } && TryReadNumeric(feature, weightField, out var numeric))
            {
                accumulator.WeightedSum += numeric;
                accumulator.WeightCount++;
            }
        }

        var output = new List<IFeature>(binOrder.Count);
        foreach (var key in binOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(bins[key].Build(weightOutputName));
        }

        return output;
    }

    private static (int Col, int Row) AssignSquare(double x, double y, double cellSize)
        => ((int)Math.Floor(x / cellSize), (int)Math.Floor(y / cellSize));

    private static Polygon BuildSquareCell(GeometryFactory factory, int col, int row, double cellSize)
    {
        var minX = col * cellSize;
        var minY = row * cellSize;
        var maxX = minX + cellSize;
        var maxY = minY + cellSize;
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    /// <summary>
    /// Pointy-top hex assignment using axial coordinates derived from the
    /// flat-axis hex grid. <c>cellSize</c> is the hex "size" (centre to
    /// corner distance); the grid is laid out so neighbours share full edges
    /// — the standard layout returned by the redblobgames hex reference.
    /// </summary>
    private static (int Col, int Row) AssignHex(double x, double y, double cellSize)
    {
        // Axial coordinates (q, r) for a pointy-top hex grid.
        var q = ((Math.Sqrt(3.0) / 3.0 * x) - (1.0 / 3.0 * y)) / cellSize;
        var r = (2.0 / 3.0 * y) / cellSize;
        return RoundAxial(q, r);
    }

    private static Polygon BuildHexCell(GeometryFactory factory, int q, int r, double cellSize)
    {
        // Convert axial (q, r) to pixel centre, then walk the six corners.
        var centreX = cellSize * Math.Sqrt(3.0) * (q + (r / 2.0));
        var centreY = cellSize * 3.0 / 2.0 * r;

        var corners = new Coordinate[7];
        for (var i = 0; i < 6; i++)
        {
            // Pointy-top corner angles: 30°, 90°, 150°, ... in degrees.
            var angle = Math.PI / 180.0 * (60.0 * i - 30.0);
            corners[i] = new Coordinate(
                centreX + cellSize * Math.Cos(angle),
                centreY + cellSize * Math.Sin(angle));
        }

        corners[6] = corners[0];
        return factory.CreatePolygon(corners);
    }

    /// <summary>
    /// Rounds fractional axial coordinates (q, r) to the nearest hex centre.
    /// Cube-coordinate rounding correction keeps q + r + s = 0 after the round.
    /// </summary>
    private static (int Col, int Row) RoundAxial(double q, double r)
    {
        var s = -q - r;
        var rq = Math.Round(q);
        var rr = Math.Round(r);
        var rs = Math.Round(s);

        var qDiff = Math.Abs(rq - q);
        var rDiff = Math.Abs(rr - r);
        var sDiff = Math.Abs(rs - s);

        if (qDiff > rDiff && qDiff > sDiff)
        {
            rq = -rr - rs;
        }
        else if (rDiff > sDiff)
        {
            rr = -rq - rs;
        }

        return ((int)rq, (int)rr);
    }

    private static bool TryReadNumeric(IFeature feature, string field, out double value)
    {
        value = 0;
        var attributes = feature.Attributes;
        if (attributes is null || !attributes.Exists(field))
        {
            return false;
        }

        var raw = attributes.GetOptionalValue(field);
        switch (raw)
        {
            case null:
                return false;
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case short s:
                value = s;
                return true;
            case decimal m:
                value = (double)m;
                return true;
            default:
                return double.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
        }
    }

    private static BinMode ReadMode(StepInputReader inputs)
    {
        var raw = inputs.GetOrDefault("mode", "hex").Trim().ToLowerInvariant();
        return raw switch
        {
            "hex" or "hexgrid" or "hex-grid" => BinMode.Hex,
            "square" or "squaregrid" or "square-grid" => BinMode.Square,
            _ => throw new TransformInputException(
                $"mode '{raw}' is not supported (allowed: hex, square)"),
        };
    }

    private static double ReadCellSize(StepInputReader inputs)
    {
        if (!inputs.TryGet("cellSize", out var raw) || string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value <= 0)
        {
            throw new TransformInputException("'cellSize' must be a finite positive number");
        }

        return value;
    }

    private enum BinMode
    {
        Hex,
        Square,
    }

    private sealed class BinAccumulator
    {
        private readonly Polygon _cell;
        private readonly int _col;
        private readonly int _row;

        public BinAccumulator(Polygon cell, int col, int row)
        {
            _cell = cell;
            _col = col;
            _row = row;
        }

        public long Count { get; set; }

        public double WeightedSum { get; set; }

        public long WeightCount { get; set; }

        public Feature Build(string? weightOutputName)
        {
            var attributes = new AttributesTable();
            attributes.Add(CellColumnAttribute, (long)_col);
            attributes.Add(CellRowAttribute, (long)_row);
            attributes.Add(CountAttribute, Count);
            if (weightOutputName is not null)
            {
                attributes.Add(weightOutputName, WeightCount > 0 ? WeightedSum : null);
            }

            return new Feature(_cell, attributes);
        }
    }
}
