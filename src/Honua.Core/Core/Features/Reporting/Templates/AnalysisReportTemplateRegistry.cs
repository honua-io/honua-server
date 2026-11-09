// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Reporting.Abstractions;

namespace Honua.Core.Features.Reporting.Templates;

internal sealed class AnalysisReportTemplateRegistry : IAnalysisReportTemplateRegistry
{
    private readonly FrozenDictionary<string, IAnalysisReportTemplate> _byProcessId;
    private readonly IAnalysisReportTemplate _fallback;

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

    public IAnalysisReportTemplate ResolveOrFallback(string processId)
    {
        if (!string.IsNullOrEmpty(processId) && _byProcessId.TryGetValue(processId, out var template))
        {
            return template;
        }

        return _fallback;
    }

    public IReadOnlyCollection<string> RegisteredProcessIds => _byProcessId.Keys;
}
