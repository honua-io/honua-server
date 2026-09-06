// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Honua.Core.Features.Attachments.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Attachments.Services;

/// <summary>
/// Default <see cref="IAttachmentOrphanLedger"/>: surfaces each orphan as an error-level
/// event and as a metric an operator can alert on.
/// </summary>
/// <remarks>
/// This deliberately does not persist to a table. Attachment orphans arise precisely when
/// one of the two stores is failing, so a ledger that needs a database write of its own
/// would be unavailable exactly when it is needed. The counter carries the orphan kind as
/// a tag so a dashboard can distinguish "we uploaded and could not clean up" from "we
/// deleted the row and could not delete the object".
/// </remarks>
public sealed class LoggingAttachmentOrphanLedger : IAttachmentOrphanLedger, IDisposable
{
    /// <summary>Meter name carrying attachment reconciliation signals.</summary>
    public const string MeterName = "Honua.Attachments";

    /// <summary>Counter name incremented once per recorded orphan.</summary>
    public const string OrphanCounterName = "honua.attachments.orphans";

    private readonly ILogger<LoggingAttachmentOrphanLedger> _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _orphans;

    /// <summary>Initializes the ledger.</summary>
    /// <param name="logger">Logger for the error-level orphan event.</param>
    public LoggingAttachmentOrphanLedger(ILogger<LoggingAttachmentOrphanLedger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _meter = new Meter(MeterName);
        _orphans = _meter.CreateCounter<long>(
            OrphanCounterName,
            unit: "{object}",
            description: "Attachment storage objects that outlived their metadata row.");
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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
