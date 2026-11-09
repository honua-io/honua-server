// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

internal static class ResponseCacheVersionKeys
{
    internal const string Prefix = "response-version:";
    internal const string RecoveryNamespace = "recovery";
    internal const string RecoveryEpoch = Prefix + RecoveryNamespace;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
}
