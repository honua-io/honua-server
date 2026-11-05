// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Collaboration.Sessions;

internal sealed partial class CollaborationSessionPruneService : BackgroundService
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(5);

    private readonly InMemoryCollaborationSessionService _sessions;
    private readonly ILogger<CollaborationSessionPruneService> _logger;

    public CollaborationSessionPruneService(
        InMemoryCollaborationSessionService sessions,
        ILogger<CollaborationSessionPruneService> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var removed = _sessions.PruneStaleParticipants(MaxIdle);
                if (removed > 0)
                {
                    Log.StaleParticipantsPruned(_logger, removed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on host shutdown.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7430,
            Level = LogLevel.Debug,
            Message = "Pruned {RemovedCount} stale collaboration session participant(s).")]
        public static partial void StaleParticipantsPruned(ILogger logger, int removedCount);
    }
}
