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

// Listing columns are copied out so the store can range-scan without unpacking the artifact, and EvidenceId is the fingerprint so a repeat upload is idempotent.
public sealed record MigrationPerformanceEvidenceRecord
{
    public required string EvidenceId { get; init; }

    public required string SourceFamily { get; init; }

    public required string FixtureSize { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string Fingerprint { get; init; }

    public required MigrationPerformanceEvidenceArtifact Artifact { get; init; }
}
