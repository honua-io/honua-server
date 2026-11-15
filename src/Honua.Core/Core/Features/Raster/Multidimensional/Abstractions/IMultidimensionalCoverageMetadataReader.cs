// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Abstractions;

// A layout that would require a whole-file read must be rejected.
public interface IMultidimensionalCoverageMetadataReader
{
    IReadOnlyList<MultidimensionalCoverageFormat> SupportedFormats { get; }

    Task<MultidimensionalCoverageMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        MultidimensionalCoverageRegistration registration,
        CancellationToken cancellationToken = default);
}
