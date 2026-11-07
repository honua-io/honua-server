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

public sealed record MigrationPerformanceEvidenceArtifact
{
    public string ArtifactKind { get; init; } = "honua.migration.performance-evidence";

    public string ArtifactVersion { get; init; } = "1.0";

    public string IssueReference { get; init; } = "honua-server#1033";

    public required string SourceFamily { get; init; }

    public required string FixtureSize { get; init; }

    public required string BaselineProfile { get; init; }

    public required string Status { get; init; }

    public required string Summary { get; init; }

    public string? RunId { get; init; }

    public required string MeasurementScope { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required MigrationFixtureSizeProfile FixtureProfile { get; init; }

    public required MigrationRunMetricsArtifact RunMetrics { get; init; }

    public required MigrationRunMetricsBaselineArtifact BaselineEvaluation { get; init; }

    public required string Fingerprint { get; init; }

    public required MigrationPerformanceEvidenceRedactionPosture Redaction { get; init; }
}

// URLs, credentials, feature payloads, and operator-identifying values are never present.
public sealed record MigrationPerformanceEvidenceRedactionPosture
{
    public bool SourceUrlsIncluded { get; init; }

    // Credential values are stripped before the artifact is emitted.
    public bool CredentialValuesIncluded { get; init; }

    public bool SourceDataIncluded { get; init; }

    public bool OperatorIdentitiesIncluded { get; init; }

    public string[] OmittedFields { get; init; } = [];

    public required string Summary { get; init; }
}
