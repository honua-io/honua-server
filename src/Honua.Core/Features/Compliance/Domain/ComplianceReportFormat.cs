// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// Output formats supported by the compliance report exporter. Only the formats
/// auditors actually consume are supported — PDF for procurement packets, CSV for
/// evidence-of-control matrices.
/// </summary>
public enum ComplianceReportFormat
{
    /// <summary>CSV evidence matrix (one row per control × evidence pair).</summary>
    Csv = 0,

    /// <summary>Self-contained PDF report.</summary>
    Pdf = 1,
}

/// <summary>
/// Rendered compliance report ready to be sent to the client. Carries both bytes
/// and the canonical MIME type / extension so endpoint glue is mechanical.
/// </summary>
public sealed record ComplianceReportArtifact
{
    /// <summary>Format the artifact was rendered in.</summary>
    public required ComplianceReportFormat Format { get; init; }

    /// <summary>MIME type to use in the HTTP <c>Content-Type</c> header.</summary>
    public required string ContentType { get; init; }

    /// <summary>File extension (without leading dot) to use in <c>Content-Disposition</c>.</summary>
    public required string FileExtension { get; init; }

    /// <summary>Suggested filename (without extension).</summary>
    public required string FileNameStem { get; init; }

    /// <summary>The rendered bytes.</summary>
    public required byte[] Content { get; init; }
}
