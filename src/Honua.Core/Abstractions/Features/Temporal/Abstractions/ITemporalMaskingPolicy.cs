// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Abstractions;

// Historical values are masked too, so a history read cannot bypass field redaction.
public interface ITemporalMaskingPolicy
{
    bool IsFieldMasked(string serviceId, int layerId, string field);
}
