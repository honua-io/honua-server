// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

// These responses must never be stored by a client cache.
internal sealed class CredentialResponseCacheMetadata
{
    internal static CredentialResponseCacheMetadata Instance { get; } = new();

    private CredentialResponseCacheMetadata()
    {
    }
}
