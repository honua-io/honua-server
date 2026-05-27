// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Server.Features.Infrastructure.Scene;

/// <summary>
/// Configuration-bound list of hosted 3D Tiles scene datasets.
/// Bind to the <c>Scenes</c> section.
/// </summary>
internal sealed class SceneDatasetOptions
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
internal sealed class SceneDatasetEntry
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
    public AccessPolicy? AccessPolicy { get; set; }
}
