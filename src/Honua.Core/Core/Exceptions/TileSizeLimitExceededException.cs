// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Exceptions;

public sealed class TileSizeLimitExceededException : Exception
{
    private const string SafeClientMessage =
        "The encoded vector tile exceeds Limits:Tiles:MaxTileSize. Request a higher zoom level or reduce the included data.";

    public TileSizeLimitExceededException()
        : base(SafeClientMessage)
    {
    }

    // The measured sizes stay on the server; the client message does not include them.
    public TileSizeLimitExceededException(long encodedBytes, long maxTileSize)
        : base(SafeClientMessage)
    {
        EncodedBytes = encodedBytes;
        MaxTileSize = maxTileSize;
    }

    public long EncodedBytes { get; } = -1;

    public long MaxTileSize { get; } = -1;

    public TileSizeLimitExceededException(string message) : base(message)
    {
    }

    public TileSizeLimitExceededException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
