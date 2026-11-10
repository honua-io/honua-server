// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Plugins;

// A failure to write the audit log is swallowed, so recording a lifecycle transition cannot become a new failure.
internal static class PluginExecutionAudit
{
    private const string SystemActor = "system";
    private const string PluginResourceType = "plugin";
    private const string NoCorrelation = "-";

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
