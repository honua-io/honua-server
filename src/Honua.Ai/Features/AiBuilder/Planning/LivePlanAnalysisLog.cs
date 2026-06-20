// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Ai.AiBuilder.Planning;

/// <summary>
/// Structured logging for the live (provider-backed) AI-builder plan analysis
/// service. Event ID range: 6660-6669. Telemetry activities use the
/// <c>honua.planner.*</c> shape.
/// </summary>
internal static partial class LivePlanAnalysisLog
{
    [LoggerMessage(
        EventId = 6660,
        Level = LogLevel.Information,
        Message = "Live plan analysis compiled an executable plan (provider={Provider}, steps={StepCount})")]
    public static partial void Planned(ILogger logger, string provider, int stepCount);
}
