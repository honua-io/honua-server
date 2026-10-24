// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Abstractions;

/// <summary>
/// Renders a <see cref="ComplianceSnapshot"/> to a serialized artifact (CSV or PDF).
/// Multiple renderers are registered — the report orchestrator selects by
/// <see cref="ComplianceReportFormat"/>.
/// </summary>
public interface IComplianceReportRenderer
{
    /// <summary>Format produced by this renderer.</summary>
    ComplianceReportFormat Format { get; }

    /// <summary>Render the snapshot. Implementations are pure functions over the snapshot.</summary>
    ComplianceReportArtifact Render(ComplianceSnapshot snapshot);
}
