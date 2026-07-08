// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Raised when a routing request supplies a spatial reference (SRID/WKID) that is
/// out of range or unknown to the spatial backend, so it cannot be used for an
/// <c>ST_Transform</c>. This is recoverable client input - adapters should map it
/// to a 400-style "invalid spatial reference" response rather than letting a raw
/// provider/database error surface as a 500.
/// </summary>
public sealed class InvalidSpatialReferenceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidSpatialReferenceException"/>
    /// class.
    /// </summary>
    /// <param name="srid">The offending SRID/WKID value.</param>
    public InvalidSpatialReferenceException(int srid)
        : base($"Invalid or unknown spatial reference (SRID/WKID) '{srid}'. The SRID must be a positive value known to the spatial backend.")
    {
        Srid = srid;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InvalidSpatialReferenceException"/>
    /// class with an inner exception (e.g. the underlying PostGIS transform error).
    /// </summary>
    /// <param name="srid">The offending SRID/WKID value.</param>
    /// <param name="innerException">The underlying error.</param>
    public InvalidSpatialReferenceException(int srid, Exception innerException)
        : base($"Invalid or unknown spatial reference (SRID/WKID) '{srid}'. The SRID must be a positive value known to the spatial backend.", innerException)
    {
        Srid = srid;
    }

    /// <summary>
    /// Gets the offending SRID/WKID value.
    /// </summary>
    public int Srid { get; }
}
