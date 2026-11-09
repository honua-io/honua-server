// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.Attachments.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Attachments.Services;

// The ledger does not write a database row, because the orphan exists when a store is already down, and the counter unit stays object so the exported series name does not change.
public sealed class LoggingAttachmentOrphanLedger : IAttachmentOrphanLedger, IDisposable
{
    public const string MeterName = "Honua.Attachments";

    public const string OrphanCounterName = "honua.attachments.orphans";

    private readonly ILogger<LoggingAttachmentOrphanLedger> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _orphans;

    public LoggingAttachmentOrphanLedger(ILogger<LoggingAttachmentOrphanLedger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _meter = new Meter(MeterName);
        _orphans = _meter.CreateCounter<long>(
            OrphanCounterName,
            unit: "{object}",
            description: "Attachment storage objects that outlived their metadata row.");
    }

    public ValueTask RecordAsync(AttachmentOrphan orphan, CancellationToken cancellationToken = default)
    {
        _orphans.Add(1, new KeyValuePair<string, object?>("kind", orphan.Kind.ToString()));
        AttachmentOrphanLog.OrphanRecorded(
            _logger,
            orphan.StoragePath,
            orphan.Kind.ToString(),
            orphan.LayerId,
            orphan.FeatureId,
            orphan.Reason ?? "(none)");
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _meter.Dispose();
}

internal static partial class AttachmentOrphanLog
{
    [LoggerMessage(
        EventId = 5505,
        Level = LogLevel.Error,
        Message = "Attachment storage object {StoragePath} is orphaned ({Kind}) for layer {LayerId} feature {FeatureId} and requires reconciliation: {Reason}")]
    public static partial void OrphanRecorded(
        ILogger logger,
        string storagePath,
        string kind,
        int layerId,
        long featureId,
        string reason);
}
