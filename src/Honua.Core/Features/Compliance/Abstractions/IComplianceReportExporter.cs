// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Orchestrates compliance report production — collects a fresh snapshot and
/// dispatches it to the renderer for the requested format.
/// </summary>
public interface IComplianceReportExporter
{
    /// <summary>Produce a rendered compliance report in the requested format.</summary>
    Task<ComplianceReportArtifact> ExportAsync(ComplianceReportFormat format, CancellationToken cancellationToken = default);
}
