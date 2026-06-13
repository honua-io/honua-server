// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Thrown when a single feature's geometry exceeds the configured import size guard (vertices, rings,
/// or serialized WKB bytes) and the import is configured to fail rather than skip such features.
/// </summary>
/// <remarks>
/// Surfaced as a 413-style, machine-readable error (<see cref="ImportValidationErrorCodes.GeometryTooLarge"/>)
/// so callers can react programmatically. The guard rejects the feature deterministically instead of
/// letting an island-scale multipolygon OOM-crash a memory-constrained host (issue #1626).
/// </remarks>
public sealed class ImportGeometryTooLargeException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImportGeometryTooLargeException"/> class.
    /// </summary>
    /// <param name="message">A human-readable description of which limit was exceeded and the guidance to fix it.</param>
    public ImportGeometryTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportGeometryTooLargeException"/> class.
    /// </summary>
    /// <param name="message">A human-readable description of which limit was exceeded.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public ImportGeometryTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
