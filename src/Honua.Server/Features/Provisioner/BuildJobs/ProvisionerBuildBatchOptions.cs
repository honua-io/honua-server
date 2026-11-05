// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Provisioner.BuildJobs;

internal sealed class ProvisionerBuildBatchOptions
{
    public const string SectionName = "Provisioner:BuildJobs:Batch";

    public bool Enabled { get; set; }

    public string Backend { get; set; } = "local";

    public BatchComputeTargetKind TargetKind { get; set; } = BatchComputeTargetKind.KubernetesJob;

    public string? Artifact { get; set; }

    public string? GeocoderArtifact { get; set; }

    public string? RouterArtifact { get; set; }

    public string? RuntimeProfile { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}
