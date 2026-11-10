// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface ICogMetadataReader
{
    Task<CogMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        CancellationToken cancellationToken = default);
}
