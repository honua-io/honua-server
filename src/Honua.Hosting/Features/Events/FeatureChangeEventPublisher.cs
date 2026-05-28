// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// Persists normalized feature-change notifications for replay and webhook delivery.
/// </summary>
internal sealed partial class FeatureChangeEventPublisher(
    IFeatureChangeEventStore store,
    ILogger<FeatureChangeEventPublisher> logger) : IFeatureChangeEventPublisher
{
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<FeatureChangeEventPublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.AppendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, ex);
        }
    }

    public Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        // The simple publisher only durably appends — there is no fallback to swallow,
        // so the strict and best-effort paths only differ in how PublishAsync handles
        // exceptions. Surface store failures here so the outbox dispatcher can keep
        // the outbox row claimed/failed instead of marking it dispatched.
        return _store.AppendAsync(request, cancellationToken);
    }

    [LoggerMessage(EventId = 9100, Level = LogLevel.Warning, Message = "Failed to publish feature-change event.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}
