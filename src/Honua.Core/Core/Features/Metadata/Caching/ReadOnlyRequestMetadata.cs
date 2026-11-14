// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Caching;

// Endpoint metadata for a POST route that only reads (a query or search whose parameters do not fit a
// URL). Such a request may use the metadata snapshot this node checked recently, as a GET does, instead
// of reading the current catalog revision on every call. Admin routes ignore the marker.
public sealed class ReadOnlyRequestMetadata
{
    private ReadOnlyRequestMetadata()
    {
    }

    public static ReadOnlyRequestMetadata Instance { get; } = new();
}
