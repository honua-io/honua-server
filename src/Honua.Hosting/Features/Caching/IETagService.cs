// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization.Metadata;

namespace Honua.Infrastructure.Caching;

public interface IETagService
{
    string ComputeETag<T>(T obj, JsonTypeInfo<T> jsonTypeInfo);

    string ComputeETag(ReadOnlySpan<byte> data);

    string ComputeETag(string content);

    bool IsModified(string? ifNoneMatch, string currentETag);

    bool MatchesPrecondition(string? ifMatch, string currentETag);

    void SetCacheHeaders(HttpResponse response, string etag, DateTimeOffset? lastModified = null);
}
