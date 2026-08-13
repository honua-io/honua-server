// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Infrastructure.Scene;

/// <summary>
/// Configuration-bound list of hosted 3D Tiles scene datasets.
/// Bind to the <c>Scenes</c> section.
/// </summary>
public sealed class SceneDatasetOptions
{
    /// <summary>
    /// Configuration section name for appsettings.json binding.
    /// </summary>
    public const string SectionName = "Scenes";

    /// <summary>
    /// Hosted scene dataset entries. Empty by default; the scene endpoints
    /// remain registered but resolve no scenes until at least one entry is
    /// configured.
    /// </summary>
    public List<SceneDatasetEntry> Datasets { get; set; } = new();
}

/// <summary>
/// A single configuration-defined scene dataset entry.
/// </summary>
public sealed class SceneDatasetEntry
{
    /// <summary>
    /// Stable scene identifier used in URLs.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional description text.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Filesystem path to the directory containing <c>tileset.json</c>.
    /// May be absolute or relative to the application content root.
    /// </summary>
    public string AssetRoot { get; set; } = string.Empty;

    /// <summary>
    /// Filename of the root tileset document. Defaults to <c>tileset.json</c>.
    /// </summary>
    public string? TilesetFileName { get; set; }

    /// <summary>
    /// Optional access policy. When omitted, the scene is publicly readable.
    /// </summary>
    public SceneAccessPolicyOptions? AccessPolicy { get; set; }
}

/// <summary>
/// Mutable configuration-binding shape for a scene dataset access policy.
/// </summary>
/// <remarks>
/// This type is kept separate from the immutable authorization-domain
/// <see cref="Honua.Core.Features.Security.Domain.AccessPolicy"/> and is projected
/// into that domain value when the configuration registry is constructed.
/// </remarks>
public sealed class SceneAccessPolicyOptions
{
    /// <summary>
    /// Gets or sets whether anonymous read access is allowed.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Gets or sets whether anonymous write access is allowed.
    /// </summary>
    public bool AllowAnonymousWrite { get; set; }

    /// <summary>
    /// Gets or sets the role names allowed to read the scene.
    /// </summary>
    public string[]? AllowedRoles { get; set; }

    /// <summary>
    /// Gets or sets the role names allowed to write the scene.
    /// </summary>
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
