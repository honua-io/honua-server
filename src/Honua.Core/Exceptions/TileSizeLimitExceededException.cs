// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>
/// An encoded vector tile exceeds the configured byte budget.
/// </summary>
public sealed class TileSizeLimitExceededException : Exception
{
    /// <summary>Creates a tile size limit error with a safe client message.</summary>
    public TileSizeLimitExceededException()
        : base("The encoded vector tile exceeds Limits:Tiles:MaxTileSize. Request a higher zoom level or reduce the included data.")
    {
    }

    /// <summary>Creates a tile size limit error.</summary>
    /// <param name="message">The error message.</param>
    public TileSizeLimitExceededException(string message) : base(message)
    {
    }

    /// <summary>Creates a tile size limit error with an inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public TileSizeLimitExceededException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
