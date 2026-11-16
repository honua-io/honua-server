// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Infrastructure.Scene;

public sealed class SceneDatasetOptions
{
    public const string SectionName = "Scenes";

    public List<SceneDatasetEntry> Datasets { get; set; } = new();
}

public sealed class SceneDatasetEntry
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string AssetRoot { get; set; } = string.Empty;

    public string? TilesetFileName { get; set; }

    public SceneAccessPolicyOptions? AccessPolicy { get; set; }
}

public sealed class SceneAccessPolicyOptions
{
    public bool AllowAnonymous { get; set; }

    public bool AllowAnonymousWrite { get; set; }

    public string[]? AllowedRoles { get; set; }

    public string[]? AllowedWriteRoles { get; set; }

    internal AccessPolicy ToAccessPolicy()
        => new()
        {
            AllowAnonymous = AllowAnonymous,
            AllowAnonymousWrite = AllowAnonymousWrite,
            AllowedRoles = AllowedRoles is null ? null : [.. AllowedRoles],
            AllowedWriteRoles = AllowedWriteRoles is null ? null : [.. AllowedWriteRoles]
        };
}
