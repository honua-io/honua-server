// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Delivery sink for an alert dispatch channel.
/// </summary>
public interface IAlertDeliverySink
{
    /// <summary>
    /// Channel type handled by this sink.
    /// </summary>
    AlertChannelType ChannelType { get; }

    /// <summary>
    /// Delivers a dispatch item.
    /// </summary>
    /// <param name="dispatchItem">Outbox dispatch item</param>
    /// <param name="alertEvent">Event payload associated with the dispatch item</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Delivery result</returns>
    Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result returned by alert delivery sinks.
/// </summary>
public sealed record AlertDeliveryResult
{
    /// <summary>
    /// Indicates whether delivery succeeded.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Indicates whether the error is retryable when delivery failed.
    /// </summary>
    public required bool Retryable { get; init; }

    /// <summary>
    /// Optional error message.
    /// </summary>
    public string? Error { get; init; }
}
