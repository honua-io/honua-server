// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

/// <summary>
/// Template strategy that converts an <see cref="AnalysisResultPackage"/> into
/// an <see cref="AnalysisReportDraft"/> (sections plus narrative slot
/// definitions). One implementation per process family or process id; the
/// generic template handles processes without a dedicated implementation.
/// </summary>
public interface IAnalysisReportTemplate
{
    /// <summary>
    /// Process identifier this template is registered against (e.g.
    /// <c>analytics.buffer-aggregate</c>). The generic template uses
    /// <see cref="ReportingConstants.GenericTemplateId"/>'s sentinel value.
    /// </summary>
    string ProcessId { get; }

    /// <summary>Template identifier embedded into <see cref="AnalysisReport.TemplateId"/>.</summary>
    string TemplateId { get; }

    /// <summary>Template version embedded into <see cref="AnalysisReport.TemplateVersion"/>.</summary>
    string TemplateVersion { get; }

    /// <summary>
    /// Builds the structural draft for the supplied package. Pure synchronous
    /// function — narrative providers are invoked by the report builder, not
    /// by the template.
    /// </summary>
    AnalysisReportDraft Build(AnalysisResultPackage package);
}
