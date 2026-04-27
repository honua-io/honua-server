// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Reporting.Abstractions;

/// <summary>
/// Registry that resolves an <see cref="IAnalysisReportTemplate"/> for a given
/// process identifier. Misses fall back to the generic template so every
/// process family is renderable.
/// </summary>
public interface IAnalysisReportTemplateRegistry
{
    /// <summary>
    /// Returns the registered template for <paramref name="processId"/>, or
    /// the generic fallback template when no dedicated one exists.
    /// </summary>
    IAnalysisReportTemplate ResolveOrFallback(string processId);

    /// <summary>
    /// Process identifiers for which a dedicated template is registered. The
    /// generic fallback is excluded.
    /// </summary>
    IReadOnlyCollection<string> RegisteredProcessIds { get; }
}
