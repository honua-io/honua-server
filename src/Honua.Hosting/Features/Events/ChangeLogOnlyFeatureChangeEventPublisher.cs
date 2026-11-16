// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

// For hosts without streams or event sinks. The change-tracking trigger has already written each
// change to the change log in its edit transaction, which is all such a host needs.
internal sealed class ChangeLogOnlyFeatureChangeEventPublisher : IFeatureChangeEventPublisher
{
    public Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
