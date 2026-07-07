// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Real <see cref="IAdminConfigChangeApplier"/>: a payload-discriminated ops-action
/// registry. It parses the <c>{action, target, params}</c> execution payload, dispatches
/// to the matching self-healing actuator, and audits an operate-timeline event. Each
/// action is idempotent and validated <em>before</em> any side effect, so a malformed or
/// unknown payload fails closed (throws <see cref="OpsActionException"/>) and never
/// partially applies. Replaces the previous logging stub that applied nothing (#2561).
/// </summary>
internal sealed partial class OpsActionAdminConfigChangeApplier(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OpsActionAdminConfigChangeApplier> logger) : IAdminConfigChangeApplier
{
    private const int DefaultRedriveBatchLimit = 500;
    private const string ActorName = "system:ops-action";

    public async Task<string?> ApplyAsync(string? changePayload, CancellationToken cancellationToken = default)
    {
        // Parse + registry check first: fail closed before opening a scope or touching state.
        var request = OpsActionRequest.Parse(changePayload);
        if (!OpsActionCatalog.IsRegistered(request.Name))
        {
            throw new OpsActionException($"Unknown ops action '{request.Name}'.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var now = timeProvider.GetUtcNow();

        var outcome = request.Name switch
        {
            OpsActionNames.RedriveDeadLetters => await RedriveDeadLettersAsync(services, request, now, cancellationToken).ConfigureAwait(false),
            OpsActionNames.PauseChannel => await SetChannelPausedAsync(services, request, paused: true, now, cancellationToken).ConfigureAwait(false),
            OpsActionNames.ResumeChannel => await SetChannelPausedAsync(services, request, paused: false, now, cancellationToken).ConfigureAwait(false),
            OpsActionNames.TuneBoundedAdmission => TuneBoundedAdmission(services, request),
            _ => throw new OpsActionException($"Unknown ops action '{request.Name}'."),
        };

        var changeId = $"opsaction-{Guid.NewGuid():N}";
        await WriteTimelineEventAsync(services, request.Name, outcome, changeId, now, cancellationToken).ConfigureAwait(false);
        Log.OpsActionApplied(logger, request.Name, changeId);
        return changeId;
    }

    private static async Task<OpsActionOutcome> RedriveDeadLettersAsync(
        IServiceProvider services,
        OpsActionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var store = ResolveDispatchStore(services);
        var limit = request.MaxCount is > 0 ? request.MaxCount.Value : DefaultRedriveBatchLimit;
        var count = await store.RedriveDeadLettersAsync(now, limit, cancellationToken).ConfigureAwait(false);
        return new OpsActionOutcome(
            "alert-dispatch",
            "dead-letters",
            $"Redrove {count.ToString(CultureInfo.InvariantCulture)} dead-lettered alert dispatch row(s).");
    }

    private static async Task<OpsActionOutcome> SetChannelPausedAsync(
        IServiceProvider services,
        OpsActionRequest request,
        bool paused,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Channel) ||
            !AlertChannelNames.TryParse(request.Channel, out var channel))
        {
            throw new OpsActionException(
                $"The '{request.Name}' action requires a valid 'channel' (webhook, websocket, email, digest, aws_sns, azure_eventgrid, slack, microsoft_teams, aws_sqs, azure_eventhub).");
        }

        var store = ResolveDispatchStore(services);
        await store.SetChannelPausedAsync(channel, paused, now, cancellationToken).ConfigureAwait(false);
        var verb = paused ? "Paused" : "Resumed";
        return new OpsActionOutcome(
            "alert-channel",
            channel.ToExternalName(),
            $"{verb} alert delivery channel '{channel.ToExternalName()}'.");
    }

    private static OpsActionOutcome TuneBoundedAdmission(IServiceProvider services, OpsActionRequest request)
    {
        if (request.Limit is not { } limit)
        {
            throw new OpsActionException("The 'db.tune_bounded_admission' action requires an integer 'limit' parameter.");
        }

        var gate = services.GetService<IRuntimeTunableAdmissionGate>()
            ?? throw new OpsActionException("The bounded database admission gate is unavailable for the active data provider.");

        if (!gate.TrySetLimit(limit, out var error))
        {
            throw new OpsActionException(error ?? $"Admission limit {limit.ToString(CultureInfo.InvariantCulture)} is out of range.");
        }

        return new OpsActionOutcome(
            "db-admission",
            "bounded",
            $"Tuned bounded database admission target to {limit.ToString(CultureInfo.InvariantCulture)} "
            + $"(range [{gate.MinLimit.ToString(CultureInfo.InvariantCulture)}, {gate.MaxLimit.ToString(CultureInfo.InvariantCulture)}]).");
    }

    private static IAlertDispatchStore ResolveDispatchStore(IServiceProvider services)
        => services.GetService<IAlertDispatchStore>()
            ?? throw new OpsActionException("The alert dispatch store is unavailable for the active data provider.");

    // The applier is a singleton but IAuditLog is scoped, so it is resolved from the
    // per-apply scope. The audit event surfaces on the Operate event timeline as an
    // Audit-kind row (the timeline is audit-log-backed), satisfying the audited +
    // operate-timeline-event requirement in one write.
    private static async Task WriteTimelineEventAsync(
        IServiceProvider services,
        string action,
        OpsActionOutcome outcome,
        string changeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var auditLog = services.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        await auditLog.RecordAsync(
            new AuditEvent
            {
                Timestamp = now,
                EventType = AuditEventType.AdminAction,
                Actor = ActorName,
                ActorType = AuditActorType.System,
                ResourceType = outcome.ResourceType,
                ResourceId = outcome.ResourceId,
                Action = action,
                Outcome = AuditOutcome.Success,
                CorrelationId = changeId,
                Details = outcome.Summary,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct OpsActionOutcome(string ResourceType, string ResourceId, string Summary);

    private static partial class Log
    {
        [LoggerMessage(9410, LogLevel.Information, "Applied ops action {Action} as change {ChangeId}")]
        public static partial void OpsActionApplied(ILogger logger, string action, string changeId);
    }
}
