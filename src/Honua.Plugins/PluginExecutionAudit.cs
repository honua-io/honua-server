// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Plugins;

/// <summary>
/// Emits plugin execution / lifecycle audit events (issue #1562). Phase 1 audited only the
/// edit-rejection case; this helper records the fuller lifecycle — a plugin auto-disabled by the
/// failure-isolation circuit breaker, its subsequent recovery, and background-service start/stop —
/// so an operator can reconstruct what a plugin did and when it was parked, without leaking plugin
/// internals to clients.
/// </summary>
/// <remarks>
/// Lifecycle events are recorded as <see cref="AuditEventType.AdminAction"/> against a synthetic
/// <c>plugin</c> resource keyed by plugin id. The system itself is the actor (these are host-driven
/// transitions, not user actions). Failures to write the audit log are swallowed: auditing a
/// best-effort lifecycle transition must never become a new failure source.
/// </remarks>
internal static class PluginExecutionAudit
{
    private const string SystemActor = "system";
    private const string PluginResourceType = "plugin";
    private const string NoCorrelation = "-";

    /// <summary>Audits a plugin being auto-disabled by the circuit breaker after repeated faults.</summary>
    public static Task RecordAutoDisabledAsync(
        IAuditLog auditLog,
        string pluginId,
        string extensionPoint,
        CancellationToken cancellationToken)
        => RecordAsync(
            auditLog,
            pluginId,
            action: "plugin.autodisabled",
            outcome: AuditOutcome.Failure,
            details: $"Plugin '{pluginId}' {extensionPoint} extension point auto-disabled after repeated failures.",
            cancellationToken);

    /// <summary>Audits a previously auto-disabled plugin recovering and being re-enabled.</summary>
    public static Task RecordRecoveredAsync(
        IAuditLog auditLog,
        string pluginId,
        string extensionPoint,
        CancellationToken cancellationToken)
        => RecordAsync(
            auditLog,
            pluginId,
            action: "plugin.recovered",
            outcome: AuditOutcome.Success,
            details: $"Plugin '{pluginId}' {extensionPoint} extension point recovered and was re-enabled.",
            cancellationToken);

    /// <summary>Audits a plugin background service starting.</summary>
    public static Task RecordBackgroundStartedAsync(
        IAuditLog auditLog,
        string pluginId,
        CancellationToken cancellationToken)
        => RecordAsync(
            auditLog,
            pluginId,
            action: "plugin.background.started",
            outcome: AuditOutcome.Success,
            details: $"Plugin '{pluginId}' background service started.",
            cancellationToken);

    /// <summary>Audits a plugin background service faulting and being disabled.</summary>
    public static Task RecordBackgroundFaultedAsync(
        IAuditLog auditLog,
        string pluginId,
        CancellationToken cancellationToken)
        => RecordAsync(
            auditLog,
            pluginId,
            action: "plugin.background.faulted",
            outcome: AuditOutcome.Failure,
            details: $"Plugin '{pluginId}' background service faulted and was disabled.",
            cancellationToken);

    private static async Task RecordAsync(
        IAuditLog auditLog,
        string pluginId,
        string action,
        AuditOutcome outcome,
        string details,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = SystemActor,
            ActorType = AuditActorType.System,
            ResourceType = PluginResourceType,
            ResourceId = pluginId,
            Action = action,
            Outcome = outcome,
            CorrelationId = NoCorrelation,
            Details = details,
        };

        try
        {
            await auditLog.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Auditing a best-effort lifecycle transition must never become a new failure source.
        }
    }
}
