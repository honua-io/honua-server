// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Reporting.Domain;

/// <summary>
/// Versioned, deterministic envelope describing the rendered shape of an
/// analytical report derived from an <see cref="AnalysisResultPackage"/>.
/// Consumed by Markdown/HTML renderers and by the MCP
/// <c>honua://jobs/{jobId}/report</c> resource.
/// </summary>
public sealed record AnalysisReport
{
    /// <summary>
    /// Stable identifier for this report. Derived from
    /// <c>(jobId, contractVersion, resultPackageId)</c> so re-rendering an
    /// unchanged result package produces a stable id.
    /// </summary>
    public required string ReportId { get; init; }

    /// <summary>
    /// Schema/contract version this report conforms to. Consumers compare
    /// against <see cref="ReportingConstants.ContractVersionV1"/>; readers that
    /// receive an unsupported version surface
    /// <see cref="ReportingConstants.UnsupportedContractVersionErrorCode"/>.
    /// </summary>
    public required string ReportContractVersion { get; init; }

    /// <summary>Originating geoprocessing job identifier.</summary>
    public required string JobId { get; init; }

    /// <summary>Originating result-package identifier.</summary>
    public required string ResultPackageId { get; init; }

    /// <summary>
    /// Process identifier, e.g. <c>analytics.buffer-aggregate</c>. Resolved by
    /// the report builder from the originating result package.
    /// </summary>
    public required string ProcessId { get; init; }

    /// <summary>
    /// Process family slice prefix, e.g. <c>analytics</c>. Surfaced separately
    /// so consumers do not have to re-parse <see cref="ProcessId"/>.
    /// </summary>
    public required string ProcessFamily { get; init; }

    /// <summary>Identifier of the template that produced this report.</summary>
    public required string TemplateId { get; init; }

    /// <summary>Version of the template that produced this report.</summary>
    public required string TemplateVersion { get; init; }

    /// <summary>Result summary copied verbatim from the source result package.</summary>
    public required ResultSummary Summary { get; init; }

    /// <summary>Ordered list of report sections.</summary>
    public required IReadOnlyList<AnalysisReportSection> Sections { get; init; }

    /// <summary>Path the narrative provider took for this report.</summary>
    public required NarrativeMode NarrativeMode { get; init; }

    /// <summary>Provenance copied verbatim from the source result package.</summary>
    public required ProvenanceRecord Provenance { get; init; }

    /// <summary>When this report envelope was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Workflow assumptions copied from the source result package.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];
}
