// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Domain;

namespace Honua.Core.Features.Reporting.Abstractions;

/// <summary>
/// Pluggable narrative provider. The deterministic provider always succeeds;
/// the LLM provider may fail or time out, in which case the report builder
/// keeps the deterministic baseline and stamps
/// <see cref="NarrativeMode.FallbackFromLlmError"/>.
/// </summary>
public interface INarrativeProvider
{
    /// <summary>
    /// True when this provider produces deterministic text. Deterministic
    /// providers never fail and short-circuit the builder's outer try/catch.
    /// </summary>
    bool IsDeterministic { get; }

    /// <summary>
    /// Generates a slot fill for the supplied draft. Implementations must
    /// honor <paramref name="cancellationToken"/>.
    /// </summary>
    Task<NarrativeFill> GenerateAsync(AnalysisReportDraft draft, CancellationToken cancellationToken);
}
