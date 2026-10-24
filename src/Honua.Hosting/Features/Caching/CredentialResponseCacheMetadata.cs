// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Identifies credential exchanges whose responses must never be stored by clients.
/// </summary>
internal sealed class CredentialResponseCacheMetadata
{
    internal static CredentialResponseCacheMetadata Instance { get; } = new();

    private CredentialResponseCacheMetadata()
    {
    }
}
