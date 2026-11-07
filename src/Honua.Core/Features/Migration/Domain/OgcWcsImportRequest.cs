// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

public sealed record OgcWcsImportRequest
{
    public required string ServiceUrl { get; init; }

    public string? Version { get; init; }

    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    // image/tiff, image/geotiff, and image/tiff parameters are ingested; every other format is manual review before any bytes are downloaded.
    public string OutputFormat { get; init; } = "image/tiff";

    public string[] CoverageSelection { get; init; } = [];

    public IReadOnlyDictionary<string, OgcCoverageImportTarget> Targets { get; init; }
        = new Dictionary<string, OgcCoverageImportTarget>(StringComparer.Ordinal);

    public bool ApplyMode { get; init; }

    // Dry-run is the default, and the apply flag must be true before any state changes.
    public bool DryRun { get; init; } = true;

    public string? TargetServiceName { get; init; }

    // The username is never echoed in the manifest.
    public string? Username { get; init; }

    // The password is never echoed in the manifest.
    public string? Password { get; init; }

    public int TimeoutSeconds { get; init; } = 120;

    public bool AllowUnsafeLocalUrls { get; init; }
}
