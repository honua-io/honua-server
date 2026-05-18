// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Multidimensional.Abstractions;

/// <summary>
/// Thrown when no registered <see cref="IMultidimensionalCoverageMetadataReader"/>
/// can service the requested container format. The default reader registered
/// by the MVP server always throws this — see ADR-0039 for the reader strategy.
/// </summary>
public sealed class MultidimensionalCoverageReaderUnavailableException : InvalidOperationException
{
    /// <summary>
    /// Stable problem code surfaced to admin/protocol clients.
    /// </summary>
    public const string ProblemCode = "HONUA-COV-HDF-READER-NOT-ENABLED";

    /// <summary>
    /// Creates a new <see cref="MultidimensionalCoverageReaderUnavailableException"/>.
    /// </summary>
    public MultidimensionalCoverageReaderUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when the registered source uses an HDF5/NetCDF4 layout that would
/// require a whole-file read (for example, contiguous unchunked datasets,
/// or chunked datasets behind filter pipelines this build cannot decode).
/// </summary>
public sealed class MultidimensionalCoverageUnsupportedLayoutException : InvalidOperationException
{
    /// <summary>
    /// Stable problem code surfaced to admin/protocol clients.
    /// </summary>
    public const string ProblemCode = "HONUA-COV-HDF-UNSUPPORTED-LAYOUT";

    /// <summary>
    /// Creates a new <see cref="MultidimensionalCoverageUnsupportedLayoutException"/>.
    /// </summary>
    public MultidimensionalCoverageUnsupportedLayoutException(string message)
        : base(message)
    {
    }
}
