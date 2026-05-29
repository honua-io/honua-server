// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Grounding.Spec;

internal static partial class SpecGroundingLog
{
    [LoggerMessage(8220, LogLevel.Debug, "Spec grounding mutate started: TurnLength={TurnLength}, HasClarificationAnswer={HasClarificationAnswer}")]
    public static partial void MutateStarted(ILogger logger, int turnLength, bool hasClarificationAnswer);

    [LoggerMessage(8221, LogLevel.Information, "Spec grounding mutate completed: MutationCount={MutationCount}, ClarificationCount={ClarificationCount}, ErrorKind={ErrorKind}")]
    public static partial void MutateCompleted(ILogger logger, int mutationCount, int clarificationCount, string? errorKind);

    [LoggerMessage(8222, LogLevel.Warning, "Spec grounding rejected: Kind={Kind}, Message={Message}")]
    public static partial void MutateRejected(ILogger logger, string kind, string message);

    [LoggerMessage(8223, LogLevel.Information, "Spec grounding summarize completed: SectionCount={SectionCount}, DurationMs={DurationMs}")]
    public static partial void SummarizeCompleted(ILogger logger, int sectionCount, double durationMs);

    [LoggerMessage(8224, LogLevel.Warning, "Spec grounding layer catalog unavailable: {Message}")]
    public static partial void CatalogUnavailable(ILogger logger, string message);
}
