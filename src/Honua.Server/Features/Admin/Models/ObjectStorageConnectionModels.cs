// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class ObjectStorageSetupResponse
{
    public required string ExternalId { get; init; }

    public string? QuickCreateUrl { get; init; }

    public required string RoleNamePrefix { get; init; }
}

public sealed class ObjectStorageConnectionCreatedResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }
}

public sealed class ObjectStorageBrowseResponse
{
    public required string Bucket { get; init; }

    public required string Prefix { get; init; }

    public IReadOnlyList<string> Folders { get; init; } = [];

    public IReadOnlyList<ObjectStorageObjectResponse> Objects { get; init; } = [];

    public bool Truncated { get; init; }
}

public sealed class ObjectStorageObjectResponse
{
    public required string Key { get; init; }

    public long? Bytes { get; init; }

    // "cog", "geotiff", "pmtiles", "geoparquet" or "other", detected from the file header.
    public required string Kind { get; init; }

    public DateTimeOffset? LastModified { get; init; }
}
