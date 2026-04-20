// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Grounding;

/// <summary>
/// Source-generated structured log methods for the grounding service. Event
/// ID range 8200-8219 — reserved directly after MCP (8150-8199) so dashboards
/// can subscribe to both ranges without collision.
/// </summary>
internal static partial class GroundingLog
{
    [LoggerMessage(8200, LogLevel.Debug, "Grounding pass started: Engine={Engine}, GoalTokens={GoalTokens}, HasHint={HasHint}")]
    public static partial void PassStarted(ILogger logger, string engine, int goalTokens, bool hasHint);

    [LoggerMessage(8201, LogLevel.Information, "Grounding pass completed: Family={Family}, Confidence={Confidence}, Candidates={CandidateCount}, ClarificationReasons={ClarificationReasons}")]
    public static partial void PassCompleted(ILogger logger, string family, double confidence, int candidateCount, int clarificationReasons);

    [LoggerMessage(8202, LogLevel.Warning, "Grounding pass rejected: Kind={Kind}, Message={Message}")]
    public static partial void PassRejected(ILogger logger, string kind, string message);

    [LoggerMessage(8203, LogLevel.Debug, "Grounding candidate filtered: Kind={Kind}, Id={Id}, Reason={Reason}")]
    public static partial void CandidateFiltered(ILogger logger, string kind, string id, string reason);
}
