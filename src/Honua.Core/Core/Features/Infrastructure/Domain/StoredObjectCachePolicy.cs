// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

// What a cache between the bucket and a client may do with a stored object. An upload that does not say is not shared.
public enum StoredObjectCachePolicy
{
    NotShared,
    PublicReplacedInPlace,
    PublicImmutable,
}

public static class StoredObjectCacheControl
{
    public const string NotShared = "private, no-store";
    public const string PublicReplacedInPlace = "public, max-age=60, s-maxage=300";
    public const string PublicImmutable = "public, max-age=31536000, immutable";

    public static string For(StoredObjectCachePolicy policy) => policy switch
    {
        StoredObjectCachePolicy.PublicReplacedInPlace => PublicReplacedInPlace,
        StoredObjectCachePolicy.PublicImmutable => PublicImmutable,
        _ => NotShared,
    };
}
