// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Services;

/// <summary>
/// Default <see cref="IMultidimensionalCoverageMetadataReader"/> registered by
/// the MVP. Always throws <see cref="MultidimensionalCoverageReaderUnavailableException"/>.
/// The reader strategy is documented in ADR-0039; this default keeps the
/// registration surface live without pulling in native HDF5 dependencies.
/// </summary>
public sealed class NotEnabledMultidimensionalCoverageMetadataReader : IMultidimensionalCoverageMetadataReader
{
    /// <inheritdoc />
    public IReadOnlyList<MultidimensionalCoverageFormat> SupportedFormats => Array.Empty<MultidimensionalCoverageFormat>();

    /// <inheritdoc />
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
