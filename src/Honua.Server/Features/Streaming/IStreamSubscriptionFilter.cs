// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

// The comparison stays synchronous and allocation-free so it does not block the broadcast path.
internal interface IStreamSubscriptionFilter
{
    bool Matches(FeatureStreamEnvelope envelope, double[]? geometryEnvelope, string? propertiesJson);

    string Summary { get; }
}
