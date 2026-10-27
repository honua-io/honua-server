// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Honua.Geoprocessing.Diagnostics;

/// <summary>
/// Versioned GP evidence attached to a diagnostic-bundle v1 envelope
/// (<c>honua.gp-diagnostic-evidence/v1</c>, honua-server#6220). Default attachments carry
/// metadata, errors, structured events, dependency and runtime identities, and input
/// references or hashes. Input bytes appear only under <see cref="InputExport"/>.
/// </summary>
internal sealed record GpDiagnosticEvidence
{
    /// <summary>Attachment schema. Independent of the envelope's <c>schemaVersion</c>.</summary>
    public const string SchemaId = "honua.gp-diagnostic-evidence/v1";

    /// <summary>Largest input this attachment will embed. Matches the diagnostic-bundle body ceiling.</summary>
    public const int MaxInputBytes = 25 * 1024 * 1024;

    public string Schema { get; init; } = SchemaId;

    public string JobId { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public string? TraceId { get; init; }

    public GpDiagnosticReleaseIdentity? Release { get; init; }

    public GpDiagnosticRuntimeIdentity? Runtime { get; init; }

    public GpDiagnosticDependency[] Dependencies { get; init; } = [];

    public GpDiagnosticError? Error { get; init; }

    public GpDiagnosticEvent[] Events { get; init; } = [];

    /// <summary>True when the projection said the event list was not the full stream.</summary>
    public bool EventsTruncated { get; init; }

    public GpDiagnosticArtifact[] Artifacts { get; init; } = [];

    public string? ArtifactsState { get; init; }

    /// <summary>Input references or hashes. Never the bytes.</summary>
    public GpDiagnosticInput[] Inputs { get; init; } = [];

    /// <summary>
    /// Explicit export choice. False on every default attachment, which then has no
    /// <see cref="InputExport"/> and no input bytes.
    /// </summary>
    public bool InputDataIncluded { get; init; }

    public GpDiagnosticInputExport? InputExport { get; init; }

    public string[] Notes { get; init; } = [];
}

/// <summary>Code and workflow identity copied from the job diagnostics projection.</summary>
internal sealed record GpDiagnosticReleaseIdentity
{
    public string State { get; init; } = "unavailable";

    public string? SourceRepository { get; init; }

    public string? SourceRef { get; init; }

    public string? WorkflowVersion { get; init; }

    public string? ProcessRelease { get; init; }

    public string? LockDigest { get; init; }

    public string[] Missing { get; init; } = [];
}

/// <summary>Runtime that executed the job. Digests stay; credentials do not.</summary>
internal sealed record GpDiagnosticRuntimeIdentity
{
    public string ServerVersion { get; init; } = string.Empty;

    public string? Runtime { get; init; }

    public string? WorkerImage { get; init; }

    public string? Entrypoint { get; init; }

    public string? DependencyLockDigest { get; init; }
}

/// <summary>One dependency identity (name, version, digest). Not the lock file bytes.</summary>
internal sealed record GpDiagnosticDependency
{
    public string Name { get; init; } = string.Empty;

    public string? Version { get; init; }

    public string? Digest { get; init; }
}

/// <summary>Sanitized failure. Stderr is prose and may have been path-scrubbed.</summary>
internal sealed record GpDiagnosticError
{
    public string Message { get; init; } = string.Empty;

    public string? Classification { get; init; }

    public string? Phase { get; init; }

    public int? StepOrdinal { get; init; }

    public string? ErrorCode { get; init; }

    public int? NativeExitCode { get; init; }

    public string? StderrExcerpt { get; init; }
}

/// <summary>One structured diagnostic event after redaction.</summary>
internal sealed record GpDiagnosticEvent
{
    public DateTimeOffset Timestamp { get; init; }

    public string Level { get; init; } = string.Empty;

    public string? Phase { get; init; }

    public string Message { get; init; } = string.Empty;

    public GpDiagnosticAttribute[] Attributes { get; init; } = [];
}

/// <summary>One redacted event attribute. Sensitive names keep the key and lose the value.</summary>
internal sealed record GpDiagnosticAttribute
{
    public string Name { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

/// <summary>An input reference or hash. This record has no byte payload.</summary>
internal sealed record GpDiagnosticInput
{
    public string Id { get; init; } = string.Empty;

    public string Role { get; init; } = "input";

    public string? MediaType { get; init; }

    public string? Reference { get; init; }

    public long? ByteSize { get; init; }

    /// <summary>Lowercase hex SHA-256 of the original bytes when those bytes were available.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>Artifact identity. Provider links are omitted; they carry signatures.</summary>
internal sealed record GpDiagnosticArtifact
{
    public string Id { get; init; } = string.Empty;

    public string? Kind { get; init; }

    public string? Availability { get; init; }

    public string? Label { get; init; }

    public long? ByteSize { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>
/// Receipt for an explicit input-data export: who authorized it, when, and which inputs
/// were actually included. Absent on a default attachment.
/// </summary>
internal sealed record GpDiagnosticInputExport
{
    public string AuthorizedBy { get; init; } = string.Empty;

    public DateTimeOffset AuthorizedAt { get; init; }

    public GpDiagnosticIncludedInput[] Included { get; init; } = [];
}

/// <summary>One input named by the authorization, with bytes only when they were supplied.</summary>
internal sealed record GpDiagnosticIncludedInput
{
    public string Id { get; init; } = string.Empty;

    public bool ContentIncluded { get; init; }

    public string? UnavailableReason { get; init; }

    public long? ByteSize { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>Lowercase hex SHA-256 of the bytes actually embedded, after redaction.</summary>
    public string? ContentSha256 { get; init; }

    public bool RedactionApplied { get; init; }

    public string? ContentBase64 { get; init; }
}

/// <summary>Unsanitized facts the attachment is built from. Not serialized.</summary>
internal sealed record GpDiagnosticEvidenceDraft
{
    public required string JobId { get; init; }

    public string? CorrelationId { get; init; }

    public string? TraceId { get; init; }

    public string ReleaseState { get; init; } = "unavailable";

    public string? SourceRepository { get; init; }

    public string? SourceRef { get; init; }

    public string? WorkflowVersion { get; init; }

    public string? ProcessRelease { get; init; }

    public string? LockDigest { get; init; }

    public IReadOnlyList<string> Missing { get; init; } = [];

    public string ServerVersion { get; init; } = string.Empty;

    public string? Runtime { get; init; }

    public string? WorkerImage { get; init; }

    public string? Entrypoint { get; init; }

    public string? DependencyLockDigest { get; init; }

    public IReadOnlyList<GpDiagnosticDependencyDraft> Dependencies { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public string? ErrorClassification { get; init; }

    public string? ErrorPhase { get; init; }

    public int? StepOrdinal { get; init; }

    public string? ErrorCode { get; init; }

    public int? NativeExitCode { get; init; }

    public string? StderrExcerpt { get; init; }

    public bool EventsTruncated { get; init; }

    public IReadOnlyList<GpDiagnosticEventDraft> Events { get; init; } = [];

    public string? ArtifactsState { get; init; }

    public IReadOnlyList<GpDiagnosticArtifactDraft> Artifacts { get; init; } = [];

    public IReadOnlyList<GpDiagnosticInputDraft> Inputs { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Dependency identity before redaction.</summary>
internal sealed record GpDiagnosticDependencyDraft
{
    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Digest { get; init; }
}

/// <summary>Structured event before redaction.</summary>
internal sealed record GpDiagnosticEventDraft
{
    public required DateTimeOffset Timestamp { get; init; }

    public string Level { get; init; } = "info";

    public string? Phase { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<GpDiagnosticAttributeDraft> Attributes { get; init; } = [];
}

/// <summary>Event attribute before redaction.</summary>
internal sealed record GpDiagnosticAttributeDraft
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

/// <summary>Artifact identity before redaction. No provider URL.</summary>
internal sealed record GpDiagnosticArtifactDraft
{
    public required string Id { get; init; }

    public string? Kind { get; init; }

    public string? Availability { get; init; }

    public string? Label { get; init; }

    public long? ByteSize { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>
/// One input. <see cref="Content"/> is hashed into the default attachment and embedded
/// only when an authorized export names this <see cref="Id"/>.
/// </summary>
internal sealed record GpDiagnosticInputDraft
{
    public required string Id { get; init; }

    public string Role { get; init; } = "input";

    public string? MediaType { get; init; }

    public string? Reference { get; init; }

    public string? Sha256 { get; init; }

    [JsonIgnore]
    public byte[]? Content { get; init; }
}

/// <summary>
/// The caller's export choice. <see cref="IncludeInputData"/> alone does not include bytes;
/// <see cref="AuthorizedBy"/> and <see cref="InputIds"/> must name the authorization.
/// </summary>
internal sealed record GpDiagnosticInputExportRequest
{
    public bool IncludeInputData { get; init; }

    public string? AuthorizedBy { get; init; }

    public DateTimeOffset? AuthorizedAt { get; init; }

    public IReadOnlyList<string>? InputIds { get; init; }
}

/// <summary>The attachment could not be read or built. Not an authorization refusal.</summary>
internal class GpDiagnosticBundleException : Exception
{
    public GpDiagnosticBundleException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Input bytes were requested, or a document already contains them, without an authorization
/// record. The message never includes those bytes.
/// </summary>
internal sealed class GpDiagnosticExportUnauthorizedException : GpDiagnosticBundleException
{
    public GpDiagnosticExportUnauthorizedException(string message)
        : base(message)
    {
    }
}
