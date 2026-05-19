// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Core.Features.Raster.Multidimensional.Abstractions;

/// <summary>
/// Reads metadata for cloud-optimized HDF5 / NetCDF4 coverage sources via
/// range requests. Implementations must reject layouts that would require a
/// whole-file read (see <see cref="MultidimensionalCoverageReaderUnavailableException"/>).
/// </summary>
public interface IMultidimensionalCoverageMetadataReader
{
    /// <summary>
    /// Container formats this reader can extract metadata for. The empty
    /// array signals the reader is the not-enabled default and will return
    /// a clear unavailability error for every input.
    /// </summary>
    IReadOnlyList<MultidimensionalCoverageFormat> SupportedFormats { get; }

    /// <summary>
    /// Reads CF-compliant coverage metadata from a cloud-hosted multidimensional source.
    /// </summary>
    /// <param name="reader">Cloud range reader for the object's storage provider.</param>
    /// <param name="registration">Registration identifying the bucket, key, format, and selected variables.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="MultidimensionalCoverageReaderUnavailableException">
    /// Thrown by the not-enabled default reader, or by a registered reader
    /// that does not support the registration's format.
    /// </exception>
    /// <exception cref="MultidimensionalCoverageUnsupportedLayoutException">
    /// Thrown when the source uses a layout that would require a whole-file read.
    /// </exception>
    Task<MultidimensionalCoverageMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        MultidimensionalCoverageRegistration registration,
        CancellationToken cancellationToken = default);
}
