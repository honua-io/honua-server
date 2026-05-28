// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Severity for metadata v1 to v2 migration diagnostics.
/// </summary>
public enum MetadataV2MigrationDiagnosticSeverity
{
    /// <summary>
    /// Informational diagnostic.
    /// </summary>
    Info,

    /// <summary>
    /// Non-blocking warning.
    /// </summary>
    Warning,

    /// <summary>
    /// Blocking diagnostic that prevents automated migration.
    /// </summary>
    Blocker
}

/// <summary>
/// Category for metadata v1 to v2 migration diagnostics.
/// </summary>
public enum MetadataV2MigrationDiagnosticKind
{
    /// <summary>
    /// Default value inferred during migration planning.
    /// </summary>
    InferredDefault,

    /// <summary>
    /// Warning that should be reviewed before migration.
    /// </summary>
    Warning,

    /// <summary>
    /// Blocker that must be resolved before migration.
    /// </summary>
    Blocker,

    /// <summary>
    /// Manual follow-up needed after or before migration.
    /// </summary>
    ManualFollowUp
}

/// <summary>
/// Diagnostic emitted by metadata v1 to v2 migration planning.
/// </summary>
public sealed record MetadataV2MigrationDiagnostic
{
    /// <summary>
    /// Stable diagnostic code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable diagnostic message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Diagnostic kind.
    /// </summary>
    public required MetadataV2MigrationDiagnosticKind Kind { get; init; }

    /// <summary>
    /// Diagnostic severity.
    /// </summary>
    public required MetadataV2MigrationDiagnosticSeverity Severity { get; init; }

    /// <summary>
    /// Optional v1 resource identifier or path.
    /// </summary>
    public string? ResourceRef { get; init; }

    /// <summary>
    /// Optional v2 field path affected by the diagnostic.
    /// </summary>
    public string? TargetPath { get; init; }

    /// <summary>
    /// Creates an inferred-default diagnostic.
    /// </summary>
    public static MetadataV2MigrationDiagnostic InferredDefault(
        string code,
        string message,
        string? resourceRef = null,
        string? targetPath = null)
        => new()
        {
            Code = code,
            Message = message,
            Kind = MetadataV2MigrationDiagnosticKind.InferredDefault,
            Severity = MetadataV2MigrationDiagnosticSeverity.Info,
            ResourceRef = resourceRef,
            TargetPath = targetPath
        };

    /// <summary>
    /// Creates a warning diagnostic.
    /// </summary>
    public static MetadataV2MigrationDiagnostic Warning(
        string code,
        string message,
        string? resourceRef = null,
        string? targetPath = null)
        => new()
        {
            Code = code,
            Message = message,
            Kind = MetadataV2MigrationDiagnosticKind.Warning,
            Severity = MetadataV2MigrationDiagnosticSeverity.Warning,
            ResourceRef = resourceRef,
            TargetPath = targetPath
        };

    /// <summary>
    /// Creates a blocker diagnostic.
    /// </summary>
    public static MetadataV2MigrationDiagnostic Blocker(
        string code,
        string message,
        string? resourceRef = null,
        string? targetPath = null)
        => new()
        {
            Code = code,
            Message = message,
            Kind = MetadataV2MigrationDiagnosticKind.Blocker,
            Severity = MetadataV2MigrationDiagnosticSeverity.Blocker,
            ResourceRef = resourceRef,
            TargetPath = targetPath
        };

    /// <summary>
    /// Creates a manual follow-up diagnostic.
    /// </summary>
    public static MetadataV2MigrationDiagnostic ManualFollowUp(
        string code,
        string message,
        MetadataV2MigrationDiagnosticSeverity severity = MetadataV2MigrationDiagnosticSeverity.Warning,
        string? resourceRef = null,
        string? targetPath = null)
        => new()
        {
            Code = code,
            Message = message,
            Kind = MetadataV2MigrationDiagnosticKind.ManualFollowUp,
            Severity = severity,
            ResourceRef = resourceRef,
            TargetPath = targetPath
        };
}

/// <summary>
/// Aggregated metadata v1 to v2 migration diagnostics.
/// </summary>
public sealed record MetadataV2MigrationDiagnosticReport
{
    /// <summary>
    /// Diagnostics in deterministic insertion order.
    /// </summary>
    public IReadOnlyList<MetadataV2MigrationDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Highest severity present in the report.
    /// </summary>
    public MetadataV2MigrationDiagnosticSeverity MaxSeverity
        => Diagnostics.Count == 0
            ? MetadataV2MigrationDiagnosticSeverity.Info
            : Diagnostics.Max(diagnostic => diagnostic.Severity);

    /// <summary>
    /// Indicates whether any blockers are present.
    /// </summary>
    public bool HasBlockers => Diagnostics.Any(diagnostic => diagnostic.Severity == MetadataV2MigrationDiagnosticSeverity.Blocker);

    /// <summary>
    /// Inferred-default diagnostics.
    /// </summary>
    public IReadOnlyList<MetadataV2MigrationDiagnostic> InferredDefaults
        => Diagnostics.Where(diagnostic => diagnostic.Kind == MetadataV2MigrationDiagnosticKind.InferredDefault).ToArray();

    /// <summary>
    /// Warning diagnostics.
    /// </summary>
    public IReadOnlyList<MetadataV2MigrationDiagnostic> Warnings
        => Diagnostics.Where(diagnostic => diagnostic.Severity == MetadataV2MigrationDiagnosticSeverity.Warning).ToArray();

    /// <summary>
    /// Blocking diagnostics.
    /// </summary>
    public IReadOnlyList<MetadataV2MigrationDiagnostic> Blockers
        => Diagnostics.Where(diagnostic => diagnostic.Severity == MetadataV2MigrationDiagnosticSeverity.Blocker).ToArray();

    /// <summary>
    /// Manual follow-up diagnostics.
    /// </summary>
    public IReadOnlyList<MetadataV2MigrationDiagnostic> ManualFollowUps
        => Diagnostics.Where(diagnostic => diagnostic.Kind == MetadataV2MigrationDiagnosticKind.ManualFollowUp).ToArray();

    /// <summary>
    /// Creates a migration diagnostic report.
    /// </summary>
    public static MetadataV2MigrationDiagnosticReport Create(IEnumerable<MetadataV2MigrationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new MetadataV2MigrationDiagnosticReport
        {
            Diagnostics = diagnostics.ToArray()
        };
    }
}
