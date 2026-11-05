// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Core.Features.Scene.Abstractions;

public interface IPointCloudDecompressorFactory
{
    IPointCloudDecompressor Create(ClaimsPrincipal principal);
}

// Native decompression and reprojection run in the external worker so the scene tiler stays managed and free of the job runtime.
public interface IPointCloudDecompressor
{
    Task<byte[]> DecompressAsync(byte[] source, string? sourceSrs, CancellationToken cancellationToken);
}

public sealed class PointCloudDecompressionException : Exception
{
    public PointCloudDecompressionException(string message)
        : base(message)
    {
    }

    public PointCloudDecompressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
