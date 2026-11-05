// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

internal sealed partial class FeatureChangeEventSinkBroadcaster
{
    private readonly IReadOnlyList<IFeatureChangeEventSink> _sinks;
    private readonly ILogger<FeatureChangeEventSinkBroadcaster> _logger;
    private readonly bool _enabled;

    public FeatureChangeEventSinkBroadcaster(
        IEnumerable<IFeatureChangeEventSink> sinks,
        IOptions<FeatureChangeEventSinkOptions> options,
        ILogger<FeatureChangeEventSinkBroadcaster> logger)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabled = options.Value?.Enabled ?? false;
        // Materialize once: the set of sinks is fixed at registration time, and
        // the publish hot path must not re-enumerate a deferred LINQ pipeline.
        _sinks = _enabled ? [.. sinks] : [];
    }

    public bool HasActiveSinks => _sinks.Count > 0;

    public void Dispatch(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureEvent);

        if (_sinks.Count == 0)
        {
            return;
        }

        foreach (var sink in _sinks)
        {
            // Each sink is observed independently. PublishToSinkAsync never throws,
            // so the discarded task carries no unobserved exception.
            _ = PublishToSinkAsync(sink, featureEvent, cancellationToken);
        }
    }

    public async Task DispatchAndWaitAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureEvent);

        if (_sinks.Count == 0)
        {
            return;
        }

        var pending = new List<Task>(_sinks.Count);
        foreach (var sink in _sinks)
        {
            pending.Add(PublishToSinkAsync(sink, featureEvent, cancellationToken));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private async Task PublishToSinkAsync(
        IFeatureChangeEventSink sink,
        FeatureChangeEvent featureEvent,
        CancellationToken cancellationToken)
    {
        var tag = new KeyValuePair<string, object?>("sink", sink.Name);
        try
        {
            await sink.PublishAsync(featureEvent, cancellationToken).ConfigureAwait(false);
            FeatureChangeEventSinkMetrics.Published.Add(1, tag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown; not a delivery failure.
        }
        // Intentional: sinks are fanned out via Task.WhenAll in PublishAsync above; one
        // sink's failure must not fault the others or the caller, so it is recorded and
        // logged rather than propagated.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureChangeEventSinkMetrics.Failed.Add(1, tag);
            LogSinkPublishFailed(_logger, sink.Name, featureEvent.EventId, ex);
        }
    }

    [LoggerMessage(
        EventId = 9400,
        Level = LogLevel.Warning,
        Message = "Feature-change event sink '{Sink}' failed to publish event {EventId}; durable storage and live streaming are unaffected.")]
    private static partial void LogSinkPublishFailed(ILogger logger, string sink, string eventId, Exception exception);
}
