// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Tiles;

public static class TileCachePublicationScope
{
    public static string Create(string publicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicationId)))
            .ToLowerInvariant();
    }
}
