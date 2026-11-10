// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

public interface IZarrPointSliceReader
{
    Task<ZarrPointSliceReadResult> ReadAsync(
        int layerId,
        double x,
        double y,
        int? inputSrid,
        IReadOnlyList<ZarrPointSliceSelection> selections,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ZarrPointSliceReadResult>> ReadBatchAsync(
        int layerId,
        IReadOnlyList<ZarrPointSliceReadRequest> requests,
        CancellationToken cancellationToken = default);
}
