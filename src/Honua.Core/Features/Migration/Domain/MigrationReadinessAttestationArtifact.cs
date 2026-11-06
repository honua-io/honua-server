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

public static class MigrationReadinessStatuses
{
    public const string Ready = "ready";

    // Cutover waits until operator review closes the cited manual-review or warning conditions.
    public const string Conditional = "conditional";

    // The source is not safe to cut over.
    public const string NotReady = "not-ready";
}

public static class MigrationReadinessReasonSeverities
{
    public const string Info = "info";

    public const string ManualReview = "manual-review";

    public const string Warn = "warn";

    public const string Fail = "fail";
}

public sealed record MigrationReadinessAttestationArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.readiness-attestation";

    public string ArtifactVersion { get; init; } = "1.0";

    public required string FixtureId { get; init; }

    public required string SourceKind { get; init; }

    public required string Status { get; init; }

    // The summary must not contain credentials.
    public required string Summary { get; init; }

    public required string ReplayToken { get; init; }

    public MigrationReadinessReason[] Reasons { get; init; } = [];

    public MigrationReadinessEvidenceCitation[] EvidenceCitations { get; init; } = [];
}

public sealed record MigrationReadinessReason
{
    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Stage { get; init; }

    public string? SourceId { get; init; }

    // The reason must not contain credentials or other secrets.
    public required string Message { get; init; }
}

public sealed record MigrationReadinessEvidenceCitation
{
    public required string Stage { get; init; }

    public required string ArtifactKind { get; init; }

    public required string ArtifactHash { get; init; }

    public string? ReplayToken { get; init; }
}
