// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Reporting.Abstractions;

namespace Honua.Core.Features.Reporting.Templates;

/// <summary>
/// Frozen registry that resolves an <see cref="IAnalysisReportTemplate"/> by
/// process id. Mirrors the per-process catalog pattern used by
/// <c>BuiltInProcessCatalog</c>: registered once at startup and then read-only.
/// </summary>
internal sealed class AnalysisReportTemplateRegistry : IAnalysisReportTemplateRegistry
{
    private readonly FrozenDictionary<string, IAnalysisReportTemplate> _byProcessId;
    private readonly IAnalysisReportTemplate _fallback;

    /// <summary>
    /// Builds a registry from the supplied templates. The first template whose
    /// <see cref="IAnalysisReportTemplate.ProcessId"/> equals <c>"*"</c> is
    /// treated as the fallback; consumers may register at most one fallback.
    /// </summary>
    public AnalysisReportTemplateRegistry(IEnumerable<IAnalysisReportTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var dict = new Dictionary<string, IAnalysisReportTemplate>(StringComparer.Ordinal);
        IAnalysisReportTemplate? fallback = null;
        foreach (var template in templates)
        {
            if (string.Equals(template.ProcessId, "*", StringComparison.Ordinal))
            {
                if (fallback is not null)
                {
                    throw new InvalidOperationException(
                        "Multiple fallback analysis-report templates registered. Only one template may use the '*' process id.");
                }
                fallback = template;
                continue;
            }

            if (dict.ContainsKey(template.ProcessId))
            {
                throw new InvalidOperationException(
                    $"Duplicate analysis-report template registered for process '{template.ProcessId}'.");
            }
            dict[template.ProcessId] = template;
        }

        _fallback = fallback ?? throw new InvalidOperationException(
            "No fallback analysis-report template registered. Register a template with ProcessId='*'.");
        _byProcessId = dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IAnalysisReportTemplate ResolveOrFallback(string processId)
    {
        if (!string.IsNullOrEmpty(processId) && _byProcessId.TryGetValue(processId, out var template))
        {
            return template;
        }

        return _fallback;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> RegisteredProcessIds => _byProcessId.Keys;
}
