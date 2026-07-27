// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Periodically prunes stale collaboration participants from the in-memory session transport.
/// Participants accumulate in the singleton <see cref="InMemoryCollaborationSessionService"/> on
/// every join, and a client can vanish without calling the leave endpoint or closing its socket
/// cleanly, so without this sweep a participant that stops polling would hold its presence (and
/// its event outbox) for the process lifetime.
/// </summary>
internal sealed partial class CollaborationSessionPruneService : BackgroundService
{
    /// <summary>How often stale participants are swept.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Participants idle longer than this are considered stale and removed.</summary>
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
