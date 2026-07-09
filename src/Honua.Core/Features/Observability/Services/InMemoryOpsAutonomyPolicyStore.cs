// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Services;

/// <summary>
/// In-memory <see cref="IOpsAutonomyPolicyStore"/> for tests and non-durable hosts.
/// </summary>
public sealed class InMemoryOpsAutonomyPolicyStore : IOpsAutonomyPolicyStore
{
    private readonly ConcurrentDictionary<string, OpsAutonomyPolicy> _policies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TrackState> _tracks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActionState> _actions = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private OpsAutonomySettings _settings = new();

    /// <inheritdoc />
    public Task<OpsAutonomyPolicy?> GetPolicyAsync(string rule, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        _policies.TryGetValue(rule, out var policy);
        return Task.FromResult(policy);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OpsAutonomyPolicySnapshot>> ListPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = _policies.Values
            .OrderBy(static policy => policy.Rule, StringComparer.Ordinal)
            .Select(policy => new OpsAutonomyPolicySnapshot
            {
                Policy = policy,
                TrackRecord = GetTrack(policy.Rule).ToRecord(policy.Rule),
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<OpsAutonomyPolicySnapshot>>(snapshots);
    }

    /// <inheritdoc />
    public Task<OpsAutonomyPolicySnapshot> SetPolicyAsync(
        OpsAutonomyPolicy policy,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Rule);

        var now = DateTimeOffset.UtcNow;
        var stored = Normalize(policy) with
        {
            UpdatedAt = now,
            UpdatedBy = string.IsNullOrWhiteSpace(changedBy) ? "unknown" : changedBy,
        };
        _policies[stored.Rule] = stored;
        var track = Touch(stored.Rule, now);
        return Task.FromResult(new OpsAutonomyPolicySnapshot
        {
            Policy = stored,
            TrackRecord = track.ToRecord(stored.Rule),
        });
    }

    /// <inheritdoc />
    public Task<OpsAutonomySettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_settings);

    /// <inheritdoc />
    public Task<OpsAutonomySettings> SetSettingsAsync(
        OpsAutonomySettings settings,
        string changedBy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        _settings = settings with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = string.IsNullOrWhiteSpace(changedBy) ? "unknown" : changedBy,
        };
        return Task.FromResult(_settings);
    }

    /// <inheritdoc />
    public Task<OpsAutonomyReservationResult> TryReserveAutoActionAsync(
        OpsAutonomyReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FindingId);

        lock (_lock)
        {
            if (_actions.Values.Any(action =>
                    string.Equals(action.FindingId, request.FindingId, StringComparison.Ordinal)))
            {
                return Task.FromResult(new OpsAutonomyReservationResult
                {
                    Reserved = false,
                    Reason = "finding-already-reserved",
                });
            }

            var now = DateTimeOffset.UtcNow;
            var cutoff = now - NormalizeWindow(request.Window);
            var recentCount = _actions.Values.Count(action =>
                string.Equals(action.Rule, request.Rule, StringComparison.Ordinal) &&
                action.ReservedAt >= cutoff);
            if (recentCount >= Math.Max(1, request.MaxAutoActionsPerWindow))
            {
                return Task.FromResult(new OpsAutonomyReservationResult
                {
                    Reserved = false,
                    Reason = "rate-limit-exceeded",
                });
            }

            var reservationId = $"auto-{Guid.NewGuid():N}";
            _actions[reservationId] = new ActionState(request.Rule, request.FindingId, now);
            Touch(request.Rule, now);
            return Task.FromResult(new OpsAutonomyReservationResult
            {
                Reserved = true,
                ReservationId = reservationId,
            });
        }
    }

    /// <inheritdoc />
    public Task RecordAutoActionOutcomeAsync(
        string reservationId,
        OpsAutonomyActionOutcome outcome,
        string? operationId = null,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        lock (_lock)
        {
            if (!_actions.TryGetValue(reservationId, out var action) || action.Outcome is not null)
            {
                return Task.CompletedTask;
            }

            var now = DateTimeOffset.UtcNow;
            _actions[reservationId] = action with { Outcome = outcome, CompletedAt = now };
            var track = Touch(action.Rule, now);
            switch (outcome)
            {
                case OpsAutonomyActionOutcome.Succeeded:
                    track.AutoApplied++;
                    break;
                case OpsAutonomyActionOutcome.RolledBack:
                    track.RolledBack++;
                    break;
                default:
                    track.Failed++;
                    break;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task IncrementProposalRaisedAsync(string rule, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        lock (_lock)
        {
            Touch(rule, DateTimeOffset.UtcNow).ProposalsRaised++;
        }

        return Task.CompletedTask;
    }

    private static OpsAutonomyPolicy Normalize(OpsAutonomyPolicy policy)
        => policy with
        {
            MaxAutoActionsPerWindow = Math.Max(1, policy.MaxAutoActionsPerWindow),
            Window = NormalizeWindow(policy.Window),
            MaxBlastRadius = Math.Max(1, policy.MaxBlastRadius),
        };

    private static TimeSpan NormalizeWindow(TimeSpan window)
        => window <= TimeSpan.Zero ? TimeSpan.FromHours(1) : window;

    private TrackState Touch(string rule, DateTimeOffset now)
    {
        var track = GetTrack(rule);
        track.FirstActivityAt ??= now;
        track.LastActivityAt = now;
        return track;
    }

    private TrackState GetTrack(string rule)
        => _tracks.GetOrAdd(rule, static _ => new TrackState());

    private sealed class TrackState
    {
        public long ProposalsRaised { get; set; }

        public long AutoApplied { get; set; }

        public long RolledBack { get; set; }

        public long Failed { get; set; }

        public DateTimeOffset? FirstActivityAt { get; set; }

        public DateTimeOffset? LastActivityAt { get; set; }

        public OpsAutonomyTrackRecord ToRecord(string rule) => new()
        {
            Rule = rule,
            ProposalsRaised = ProposalsRaised,
            AutoApplied = AutoApplied,
            RolledBack = RolledBack,
            Failed = Failed,
            FirstActivityAt = FirstActivityAt,
            LastActivityAt = LastActivityAt,
        };
    }

    private sealed record ActionState(
        string Rule,
        string FindingId,
        DateTimeOffset ReservedAt)
    {
        public OpsAutonomyActionOutcome? Outcome { get; init; }

        public DateTimeOffset? CompletedAt { get; init; }
    }
}
