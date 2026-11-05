// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

// A sink sees only a durably stored event, and a sink failure must not break the originating write or a sibling sink.
internal interface IFeatureChangeEventSink
{
    string Name { get; }

    Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default);
}
