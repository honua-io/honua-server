// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public sealed record GeoServerReconciliationBundle
{
    public required string RunId { get; init; }

    public required string PlanId { get; init; }

    public required string PlanDigest { get; init; }

    public string Service { get; init; } = "geoserver";

    public required GeoServerReconciliationSnapshot SourceSnapshot { get; init; }

    public required GeoServerReconciliationSnapshot TargetSnapshot { get; init; }
}

public sealed record GeoServerReconciliationSnapshot
{
    public required string RunId { get; init; }

    public required string PlanId { get; init; }

    public required string PlanDigest { get; init; }

    public string Service { get; init; } = "geoserver";

    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string[]> Schemas { get; init; } = new Dictionary<string, string[]>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, GeoServerReconciliationExtent> Extents { get; init; } =
        new Dictionary<string, GeoServerReconciliationExtent>(StringComparer.Ordinal);

    public string[] Samples { get; init; } = [];

    public string[] Relationships { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string[] SupportedMetadata { get; init; } = [];
}

public sealed record GeoServerReconciliationExtent
{
    public double MinX { get; init; }

    public double MinY { get; init; }

    public double MaxX { get; init; }

    public double MaxY { get; init; }

    public string? Crs { get; init; }
}
