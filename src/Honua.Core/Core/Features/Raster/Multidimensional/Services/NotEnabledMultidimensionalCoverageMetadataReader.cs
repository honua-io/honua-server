// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Services;

public sealed class NotEnabledMultidimensionalCoverageMetadataReader : IMultidimensionalCoverageMetadataReader
{
    public IReadOnlyList<MultidimensionalCoverageFormat> SupportedFormats => Array.Empty<MultidimensionalCoverageFormat>();

    public Task<MultidimensionalCoverageMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        MultidimensionalCoverageRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        throw new MultidimensionalCoverageReaderUnavailableException(
            $"Cloud-optimized {registration.Format} metadata extraction is not enabled in this build. " +
            "See ADR-0039 for the reader strategy and follow-up issue tracker.");
    }
}
