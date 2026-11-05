// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

// The probe is one short read with no session state, so it does not pin a pooled connection.
public interface IMetadataV2GraphRevisionProbe
{
    ValueTask<string?> GetCurrentEtagAsync(CancellationToken cancellationToken = default);
}
