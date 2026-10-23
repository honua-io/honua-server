// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
