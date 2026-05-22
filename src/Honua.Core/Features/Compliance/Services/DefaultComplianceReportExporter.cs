// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Domain;

namespace Honua.Core.Features.Compliance.Services;

/// <summary>
/// Orchestrator that collects a fresh snapshot and routes it to the renderer for
/// the requested format. Throws <see cref="NotSupportedException"/> for unknown
/// formats so endpoint adapters can return a deterministic <c>406</c>.
/// </summary>
internal sealed class DefaultComplianceReportExporter : IComplianceReportExporter
{
    private readonly IComplianceEvidenceCollector _collector;
    private readonly Dictionary<ComplianceReportFormat, IComplianceReportRenderer> _renderers;

    public DefaultComplianceReportExporter(
        IComplianceEvidenceCollector collector,
        IEnumerable<IComplianceReportRenderer> renderers)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(renderers);
        _collector = collector;
        _renderers = renderers.ToDictionary(static r => r.Format);
    }

    public async Task<ComplianceReportArtifact> ExportAsync(
        ComplianceReportFormat format,
        CancellationToken cancellationToken = default)
    {
        if (!_renderers.TryGetValue(format, out var renderer))
        {
            throw new NotSupportedException(
                $"Compliance report format '{format}' is not supported by this deployment.");
        }

        var snapshot = await _collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        return renderer.Render(snapshot);
    }
}
