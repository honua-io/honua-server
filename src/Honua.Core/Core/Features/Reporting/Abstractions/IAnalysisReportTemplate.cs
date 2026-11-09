// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

public interface IAnalysisReportTemplate
{
    string ProcessId { get; }

    string TemplateId { get; }

    string TemplateVersion { get; }

    AnalysisReportDraft Build(AnalysisResultPackage package);
}
