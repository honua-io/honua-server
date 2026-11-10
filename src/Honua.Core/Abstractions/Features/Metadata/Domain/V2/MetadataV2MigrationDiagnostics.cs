// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public enum MetadataV2MigrationDiagnosticSeverity
{
    Info,

    Warning,

    Blocker
}

public enum MetadataV2MigrationDiagnosticKind
{
    InferredDefault,

    Warning,

    Blocker,

    ManualFollowUp
}

public sealed record MetadataV2MigrationDiagnostic
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public required MetadataV2MigrationDiagnosticKind Kind { get; init; }

    public required MetadataV2MigrationDiagnosticSeverity Severity { get; init; }

    public string? ResourceRef { get; init; }

    public string? TargetPath { get; init; }

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

public sealed record MetadataV2MigrationDiagnosticReport
{
    public IReadOnlyList<MetadataV2MigrationDiagnostic> Diagnostics { get; init; } = [];

    public MetadataV2MigrationDiagnosticSeverity MaxSeverity
        => Diagnostics.Count == 0
            ? MetadataV2MigrationDiagnosticSeverity.Info
            : Diagnostics.Max(diagnostic => diagnostic.Severity);

    public bool HasBlockers => Diagnostics.Any(diagnostic => diagnostic.Severity == MetadataV2MigrationDiagnosticSeverity.Blocker);

    public IReadOnlyList<MetadataV2MigrationDiagnostic> InferredDefaults
        => Diagnostics.Where(diagnostic => diagnostic.Kind == MetadataV2MigrationDiagnosticKind.InferredDefault).ToArray();

    public IReadOnlyList<MetadataV2MigrationDiagnostic> Warnings
        => Diagnostics.Where(diagnostic => diagnostic.Severity == MetadataV2MigrationDiagnosticSeverity.Warning).ToArray();

    public IReadOnlyList<MetadataV2MigrationDiagnostic> Blockers
        => Diagnostics.Where(diagnostic => diagnostic.Severity == MetadataV2MigrationDiagnosticSeverity.Blocker).ToArray();

    public IReadOnlyList<MetadataV2MigrationDiagnostic> ManualFollowUps
        => Diagnostics.Where(diagnostic => diagnostic.Kind == MetadataV2MigrationDiagnosticKind.ManualFollowUp).ToArray();

    public static MetadataV2MigrationDiagnosticReport Create(IEnumerable<MetadataV2MigrationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new MetadataV2MigrationDiagnosticReport
        {
            Diagnostics = diagnostics.ToArray()
        };
    }
}
