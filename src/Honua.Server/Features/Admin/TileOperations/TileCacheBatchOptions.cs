// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed class TileCacheBatchOptions
{
    public const string SectionName = "TileOperations:Batch";

    public bool Enabled { get; set; }

    public string Backend { get; set; } = "local";

    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    public string? Artifact { get; set; }

    public string? RuntimeProfile { get; set; }

    // The in-process path caps at 5,000 tiles to protect the serving process; the batch ceiling is only a runaway guard.
    public int MaxTiles { get; set; } = 1_000_000;

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}
