// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed class TileCacheBatchOptions
{
    public const string SectionName = "TileOperations:Batch";

    public string Backend { get; set; } = "local";

    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    public string? Artifact { get; set; }

    public string? RuntimeProfile { get; set; }

    // A runaway guard for remote backends. The local backend runs in a serving process and keeps TileCacheJobExecutor.LocalMaxTiles.
    public int MaxTiles { get; set; } = 1_000_000;

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}
