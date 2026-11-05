// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

internal sealed class NoOpFeatureChangeEventSink : IFeatureChangeEventSink
{
    public string Name => "noop";

    public Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
