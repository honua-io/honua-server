// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Tiles;

public interface ITileMatrixSetRegistry
{
    IReadOnlyList<TileMatrixSetEntry> All { get; }

    bool IsSupported(string tileMatrixSetId);

    bool TryGet(string tileMatrixSetId, [NotNullWhen(true)] out TileMatrixSetEntry? entry);

    // Built-in gridset levels use the canonical OGC formulas so the advertised geometry stays byte-identical.
    bool TryGetGeometry(string tileMatrixSetId, int maxLevel, [NotNullWhen(true)] out GridGeometry? geometry);
}

public sealed record TileMatrixSetEntry
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Uri { get; init; }

    public required string Crs { get; init; }

    public required int Srid { get; init; }

    public required bool IsGeographic { get; init; }

    public required bool IsBuiltIn { get; init; }
}
