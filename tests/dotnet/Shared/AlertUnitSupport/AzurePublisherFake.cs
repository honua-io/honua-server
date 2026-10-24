// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Alerts;

namespace Honua.Server.Tests.Features.Alerts;

internal sealed class FakeEventHubPublisher : IEventHubPublisher
{
    public Dictionary<string, object>? LastProperties { get; private set; }

    public Func<BinaryData, Dictionary<string, object>, CancellationToken, Task<EventHubPublishResult>>? SendAsyncHandler { get; init; }

    public Task<EventHubPublishResult> SendAsync(
        BinaryData body,
        Dictionary<string, object> properties,
        CancellationToken cancellationToken)
    {
        LastProperties = properties;

        return SendAsyncHandler is null
            ? Task.FromResult(new EventHubPublishResult(true, false, null))
            : SendAsyncHandler(body, properties, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
