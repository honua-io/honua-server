// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

/// <summary>
/// An encoded vector tile exceeds the configured byte budget.
/// </summary>
public sealed class TileSizeLimitExceededException : Exception
{
    private const string SafeClientMessage =
        "The encoded vector tile exceeds Limits:Tiles:MaxTileSize. Request a higher zoom level or reduce the included data.";

    /// <summary>Creates a tile size limit error with a safe client message.</summary>
    public TileSizeLimitExceededException()
        : base(SafeClientMessage)
    {
    }

    /// <summary>
    /// Creates a tile size limit error that carries the measurement behind the refusal. The
    /// client message is unchanged — the numbers are for the server-side log an operator reads
    /// to size <c>Limits:Tiles:MaxTileSize</c>, and are never returned to the caller.
    /// </summary>
    /// <param name="encodedBytes">The encoded tile length that exceeded the budget.</param>
    /// <param name="maxTileSize">The enforced <c>Limits:Tiles:MaxTileSize</c> budget in bytes.</param>
    public TileSizeLimitExceededException(long encodedBytes, long maxTileSize)
        : base(SafeClientMessage)
    {
        EncodedBytes = encodedBytes;
        MaxTileSize = maxTileSize;
    }

    /// <summary>The encoded tile length in bytes, or <c>-1</c> when the caller did not measure it.</summary>
    public long EncodedBytes { get; } = -1;

    /// <summary>The enforced byte budget, or <c>-1</c> when the caller did not record it.</summary>
    public long MaxTileSize { get; } = -1;

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
