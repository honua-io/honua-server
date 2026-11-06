// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.StaticMap;

internal sealed class StaticMapParameters
{
    public double? Lon { get; init; }

    public double? Lat { get; init; }

    public int? Zoom { get; init; }

    public string? Bbox { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string Format { get; init; } = "png";

    public int Dpi { get; init; } = 72;

    public string? Layers { get; init; }

    public string? Markers { get; init; }

    public string? Path { get; init; }

    public string? Filter { get; init; }
}

internal static class StaticMapEditionLimits
{
    public const int CommunityMaxDimension = 1280;

    public const int CommunityMaxDpi = 72;

    public const int ProMaxDimension = 4096;

    public const int ProMaxDpi = 300;

    public const int CommunityMaxMarkers = 10;

    public const int CommunityMaxPathVertices = 20;

    public const int ProMaxMarkers = 100;

    public const int ProMaxPathVertices = 500;
}

internal readonly record struct MapMarker(double Lon, double Lat, string Color);

internal sealed class MapPath
{
    public required (double Lon, double Lat)[] Vertices { get; init; }

    public string Color { get; init; } = "#FF0000";

    public float StrokeWidth { get; init; } = 2f;
}
