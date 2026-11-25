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
                try
                {
                    _sessions.PruneRemotePresence();
                    var removed = _sessions.PruneStaleParticipants(MaxIdle);
                    if (removed > 0)
                    {
                        Log.StaleParticipantsPruned(_logger, removed);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.PruneSweepFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
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

        [LoggerMessage(
            EventId = 7431,
            Level = LogLevel.Error,
            Message = "Collaboration session prune sweep failed; continuing on next interval.")]
        public static partial void PruneSweepFailed(ILogger logger, Exception exception);
    }
}
