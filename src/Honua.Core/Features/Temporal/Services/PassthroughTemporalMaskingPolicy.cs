// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;

namespace Honua.Core.Features.Temporal.Services;

public sealed class PassthroughTemporalMaskingPolicy : ITemporalMaskingPolicy
{
    public bool IsFieldMasked(string serviceId, int layerId, string field) => false;
}
