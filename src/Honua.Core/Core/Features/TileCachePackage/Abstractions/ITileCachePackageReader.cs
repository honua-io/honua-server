// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.TileCachePackage.Domain;

namespace Honua.Core.Features.TileCachePackage.Abstractions;

public interface ITileCachePackageReader
{
    bool CanRead(string fileName);

    Task<TileCachePackageDescriptor> ReadDescriptorAsync(
        Stream package,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TileCachePackageTile> ReadTilesAsync(
        Stream package,
        TileCachePackageDescriptor descriptor,
        int minZoom,
        int maxZoom,
        CancellationToken cancellationToken = default);
}
