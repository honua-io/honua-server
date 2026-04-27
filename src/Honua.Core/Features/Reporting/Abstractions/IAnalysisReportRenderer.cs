// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

/// <summary>
/// Renders an <see cref="AnalysisReport"/> to a serialized document body in a
/// specific <see cref="AnalysisReportFormat"/>.
/// </summary>
public interface IAnalysisReportRenderer
{
    /// <summary>Format produced by this renderer.</summary>
    AnalysisReportFormat Format { get; }

    /// <summary>
    /// Renders the report to its final form. Renderers refuse contract
    /// versions they do not support by raising
    /// <see cref="UnsupportedReportContractVersionException"/>.
    /// </summary>
    string Render(AnalysisReport report);
}
