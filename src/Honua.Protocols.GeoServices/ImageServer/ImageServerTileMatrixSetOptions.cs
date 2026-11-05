// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Honua.Protocols.GeoServices.ImageServer;

// An empty gridset list advertises only WebMercatorQuad, and the list stays empty through a rolling deployment so a new replica cannot advertise a matrix set an older replica cannot serve.
public sealed class ImageServerTileMatrixSetOptions
{
    public const string SectionName = "GeoServices:ImageServer:TileMatrixSets";

    public IList<string> Enabled { get; } = new List<string>();
}
