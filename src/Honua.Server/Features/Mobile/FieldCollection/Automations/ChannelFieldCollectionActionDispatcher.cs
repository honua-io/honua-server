// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

/// <summary>
/// In-process, bounded-channel <see cref="IFieldCollectionActionDispatcher"/>
/// (#2121). Enqueue hands the invocation to a background reader so the mobile push
/// response is never blocked on online action delivery. The channel is bounded and
/// uses wait back-pressure rather than dropping invocations.
/// </summary>
internal sealed class ChannelFieldCollectionActionDispatcher : IFieldCollectionActionDispatcher
{
    private readonly Channel<FieldCollectionActionInvocation> _channel;

    public ChannelFieldCollectionActionDispatcher(IOptions<FieldCollectionAutomationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = Math.Max(1, options.Value.QueueCapacity);
        _channel = Channel.CreateBounded<FieldCollectionActionInvocation>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Gets the reader drained by the background dispatch service.</summary>
    public ChannelReader<FieldCollectionActionInvocation> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(
        FieldCollectionActionInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return _channel.Writer.WriteAsync(invocation, cancellationToken);
    }
}
