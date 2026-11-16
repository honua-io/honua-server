// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IH3CapabilityChecker
{
    Task<bool?> IsH3AvailableAsync(CancellationToken cancellationToken = default);
}
