// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

public interface INarrativeProvider
{
    // A deterministic provider must not fail, and the builder does not wrap it in the fallback try.
    bool IsDeterministic { get; }

    Task<NarrativeFill> GenerateAsync(AnalysisReportDraft draft, CancellationToken cancellationToken);
}
