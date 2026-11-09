// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.MapServer.Models;

internal sealed class ExportParameters
{
    public string? Bbox { get; init; }

    public string? Size { get; init; }

    public int Dpi { get; init; } = 96;

    public string Format { get; init; } = "png";

    public bool Transparent { get; init; } = true;

    public string? Layers { get; init; }

    public string? BboxSr { get; init; }

    public string? ImageSr { get; init; }

    public string F { get; init; } = "image";

    public string? BackgroundColor { get; init; }
}
