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

public sealed record MigrationCutoverReadinessAttestationArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.cutover-readiness-attestation";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string RunId { get; init; }

    public required string SourceId { get; init; }

    public required string SourceKind { get; init; }

    public required string FixtureName { get; init; }

    // Stored as normalized ISO-8601 text so the artifact stays stable across serializers.
    public required string GeneratedAtUtc { get; init; }

    public required MigrationCutoverReadinessClassificationCounts ClassificationCounts { get; init; }
}

public sealed record MigrationCutoverReadinessClassificationCounts
{
    public int Automated { get; init; }

    public int Assisted { get; init; }

    public int ManualReview { get; init; }

    public int Unsupported { get; init; }
}
