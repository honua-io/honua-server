// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Security;

namespace Honua.Import.FileImport;

internal static class ImportObjectStoreAudit
{
    public const string ActionName = "import.object-store";

    public const string Succeeded = "succeeded";

    public const string Queued = "queued";

    public const string Forbidden = "forbidden";

    public const string NotFound = "not_found";

    public const string TooLarge = "too_large";

    public const string Changed = "changed";

    public const string Unavailable = "unavailable";

    public const string Failed = "failed";

    public const string QueueFull = "queue_full";

    internal sealed record Entry
    {
        public string? SourceUri { get; init; }

        public string? ETag { get; init; }

        public long? SizeBytes { get; init; }

        public string? ConnectionId { get; init; }

        public string? JobId { get; init; }

        public required string Outcome { get; init; }
    }

    public static async Task TryRecordAsync(
        HttpContext context,
        Entry entry,
        CancellationToken cancellationToken)
    {
        var sourceUri = entry.SourceUri;
        var outcome = entry.Outcome;
        var audit = context.RequestServices.GetService<IAuditLog>();
        if (audit is null)
        {
            return;
        }

        var identity = CanonicalSecurityActor.Resolve(context.User);
        var actorType = AuditActorType.Anonymous;
        var actor = AuditEvent.AnonymousActor;
        if (identity is not null)
        {
            actor = identity.ActorId;
            actorType = identity.ApiKeyId is null ? AuditActorType.UserId : AuditActorType.ApiKey;
        }

        var payload = new ImportObjectStoreAuditPayload
        {
            SourceUri = sourceUri,
            ETag = entry.ETag,
            SizeBytes = entry.SizeBytes,
            ConnectionId = entry.ConnectionId,
            JobId = entry.JobId,
            Outcome = outcome,
        };
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = actor,
            ActorType = actorType,
            ResourceType = "import",
            ResourceId = sourceUri,
            Action = ActionName,
            Outcome = outcome is Forbidden ? AuditOutcome.Denied
                : outcome is Succeeded or Queued ? AuditOutcome.Success
                : AuditOutcome.Failure,
            CorrelationId = string.IsNullOrWhiteSpace(context.TraceIdentifier) ? "-" : context.TraceIdentifier,
            Details = JsonSerializer.Serialize(payload, ImportJsonContext.Default.ImportObjectStoreAuditPayload),
        };

        try
        {
            _ = await audit.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImportEndpoints.ReportAuditFailure(context, ex);
        }
    }
}

internal sealed record ImportObjectStoreAuditPayload
{
    public string? SourceUri { get; init; }

    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    public long? SizeBytes { get; init; }

    public string? ConnectionId { get; init; }

    public string? JobId { get; init; }

    public required string Outcome { get; init; }
}
