// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Core.Features.Tiles;

public sealed class TileMatrixSetDefinitionOptions
{
    public const string SectionName = "TileMatrixSets";

    // The built-in gridsets are always available and must not be redefined here.
    public IList<CustomTileMatrixSet> Custom { get; set; } = new List<CustomTileMatrixSet>();
}

public sealed class CustomTileMatrixSet
{
    public string Id { get; set; } = string.Empty;

    public string Crs { get; set; } = string.Empty;

    public string? Uri { get; set; }

    public string? Title { get; set; }

    public int Srid { get; set; }

    public double? MetersPerUnit { get; set; }

    public double[] TopLeftCorner { get; set; } = [];

    public int TileWidth { get; set; } = 256;

    public int TileHeight { get; set; } = 256;

    public IList<TileMatrixLevel> Levels { get; set; } = new List<TileMatrixLevel>();
}

public sealed class TileMatrixLevel
{
    public int Id { get; set; }

    public double ScaleDenominator { get; set; }

    public double CellSize { get; set; }

    public long MatrixWidth { get; set; }

    public long MatrixHeight { get; set; }
}
