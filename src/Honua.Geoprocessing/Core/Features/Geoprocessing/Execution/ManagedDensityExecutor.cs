// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

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

    // Cell indices are stored and emitted as int (CELL_COL / CELL_ROW). Bounding
    // their magnitude well inside int range guards against overflow when a large
    // coordinate (e.g. Web Mercator X up to ~2.0037e7) is divided by a small
    // cellSize, which would otherwise wrap a double cell index into a bogus int
    // and silently bin features into the wrong cell.
    private const long MaxCellIndexMagnitude = int.MaxValue - 1;

    private static (int Col, int Row) AssignSquare(double x, double y, double cellSize)
        => (CheckedIndex(Math.Floor(x / cellSize), x, y, cellSize),
            CheckedIndex(Math.Floor(y / cellSize), x, y, cellSize));

    // The cell index is checked before the cast so a large coordinate over a small cell cannot overflow silently.
    private static int CheckedIndex(double index, double x, double y, double cellSize)
    {
        if (!double.IsFinite(index) || Math.Abs(index) > MaxCellIndexMagnitude)
        {
            throw new TransformInputException(
                $"coordinate ({x.ToString(CultureInfo.InvariantCulture)}, " +
                $"{y.ToString(CultureInfo.InvariantCulture)}) with cellSize " +
                $"{cellSize.ToString(CultureInfo.InvariantCulture)} produces an out-of-range cell index");
        }

        return (int)index;
    }

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

    private static (int Col, int Row) AssignHex(double x, double y, double cellSize)
    {
        // Axial coordinates (q, r) for a pointy-top hex grid.
        var q = ((Math.Sqrt(3.0) / 3.0 * x) - (1.0 / 3.0 * y)) / cellSize;
        var r = (2.0 / 3.0 * y) / cellSize;
        return RoundAxial(q, r, x, y, cellSize);
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

    private static (int Col, int Row) RoundAxial(double q, double r, double x, double y, double cellSize)
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

        return (CheckedIndex(rq, x, y, cellSize), CheckedIndex(rr, x, y, cellSize));
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
