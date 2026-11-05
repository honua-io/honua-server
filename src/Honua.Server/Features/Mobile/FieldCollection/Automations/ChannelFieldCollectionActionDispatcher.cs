// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

// A full channel drops the invocation instead of blocking the mobile response.
internal sealed partial class ChannelFieldCollectionActionDispatcher : IFieldCollectionActionDispatcher
{
    private readonly Channel<FieldCollectionActionInvocation> _channel;
    private readonly ILogger<ChannelFieldCollectionActionDispatcher> _logger;

    public ChannelFieldCollectionActionDispatcher(
        IOptions<FieldCollectionAutomationOptions> options,
        ILogger<ChannelFieldCollectionActionDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var capacity = Math.Max(1, options.Value.QueueCapacity);
        _channel = Channel.CreateBounded<FieldCollectionActionInvocation>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<FieldCollectionActionInvocation> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(
        FieldCollectionActionInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();

        // TryWrite does not wait when FullMode is Wait. A full queue must not
        // block the request that already committed the mobile push (#6125).
        if (!_channel.Writer.TryWrite(invocation))
        {
            LogDropped(_logger, invocation.Action.Id, invocation.InvocationId);
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(
        EventId = 21225,
        Level = LogLevel.Warning,
        Message = "FieldCollection automation queue is full; dropped invocation. ActionId={ActionId} InvocationId={InvocationId}")]
    private static partial void LogDropped(ILogger logger, string actionId, string invocationId);
}
