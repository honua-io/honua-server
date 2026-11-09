// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Reporting.Domain;

public sealed record AnalysisReport
{
    public required string ReportId { get; init; }

    public required string ReportContractVersion { get; init; }

    public required string JobId { get; init; }

    public required string ResultPackageId { get; init; }

    public required string ProcessId { get; init; }

    public required string ProcessFamily { get; init; }

    public required string TemplateId { get; init; }

    public required string TemplateVersion { get; init; }

    public required ResultSummary Summary { get; init; }

    public required IReadOnlyList<AnalysisReportSection> Sections { get; init; }

    public required NarrativeMode NarrativeMode { get; init; }

    public required ProvenanceRecord Provenance { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public IReadOnlyList<string> Assumptions { get; init; } = [];
}
