// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Persistence envelope wrapping a <see cref="MigrationPerformanceEvidenceArtifact"/>
/// with the fields the admin UI / SDKs need to list and link to a specific publish.
/// </summary>
/// <remarks>
/// <para>
/// The record is intentionally minimal: it copies the few fields the listing endpoints
/// project (source family, fixture size, status, generated-at, fingerprint) into
/// queryable columns so the store can range-scan without unpacking the full artifact
/// JSON. The full artifact remains addressable through <see cref="Artifact"/>.
/// </para>
/// <para>
/// <see cref="EvidenceId"/> is derived from the artifact fingerprint so reuploading the
/// same artifact is idempotent. Callers should not assume the value is opaque; it is
/// safe to display.
/// </para>
/// </remarks>
public sealed record MigrationPerformanceEvidenceRecord
{
    /// <summary>Stable evidence identifier, typically the artifact fingerprint.</summary>
    public required string EvidenceId { get; init; }

    /// <summary>Source family the artifact was emitted for.</summary>
    public required string SourceFamily { get; init; }

    /// <summary>Fixture size identifier the artifact was emitted for.</summary>
    public required string FixtureSize { get; init; }

    /// <summary>Aggregate run status (<c>Pass</c>, <c>Warn</c>, or <c>Fail</c>).</summary>
    public required string Status { get; init; }

    /// <summary>Wall-clock instant the artifact was emitted by the builder.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>SHA-256 fingerprint copied from the underlying artifact.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>Full website-linkable evidence artifact.</summary>
    public required MigrationPerformanceEvidenceArtifact Artifact { get; init; }
}
