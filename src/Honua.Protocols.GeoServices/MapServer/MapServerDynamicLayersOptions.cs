// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.MapServer;

/// <summary>
/// Options controlling MapServer <c>dynamicLayers</c> workspace (<c>source.type=dataLayer</c>)
/// support. Workspace data layers let a client reference a registered data source (workspace)
/// and a table at request time, instead of an existing published map layer.
/// </summary>
/// <remarks>
/// For security the workspace path never accepts an arbitrary connection string, raw SQL, or an
/// unpublished table. A <c>dataLayer</c> source is resolved against the server-side metadata graph:
/// the requested workspace must be in <see cref="AllowedWorkspaceIds"/> (or all workspaces must be
/// allowed via <see cref="AllowAllRegisteredWorkspaces"/>), and the requested table must already be
/// materialized by a published resource backed by that same workspace. The resolved resource's
/// access policy (RBAC) is then enforced through the shared query/render pipeline exactly as for a
/// normal published layer.
/// </remarks>
public sealed class MapServerDynamicLayersOptions
{
    /// <summary>
    /// Configuration section that binds these options
    /// (<c>GeoServices:MapServer:DynamicLayers</c>).
    /// </summary>
    public const string SectionName = "GeoServices:MapServer:DynamicLayers";

    /// <summary>
    /// Gets or sets a value indicating whether <c>source.type=dataLayer</c> workspace data layers
    /// are accepted. Defaults to <see langword="false"/>; <c>source.type=mapLayer</c> dynamic layers
    /// are always supported regardless of this flag.
    /// </summary>
    public bool WorkspaceLayersEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every registered workspace (data connection) is an
    /// allowed <c>dataLayer</c> source. When <see langword="false"/> (the default) only the
    /// workspace ids listed in <see cref="AllowedWorkspaceIds"/> are accepted.
    /// </summary>
    public bool AllowAllRegisteredWorkspaces { get; set; }

    /// <summary>
    /// Gets the server-side allowlist of workspace (data connection) ids that may back a
    /// <c>dataLayer</c> source. Ignored when <see cref="AllowAllRegisteredWorkspaces"/> is
    /// <see langword="true"/>.
    /// </summary>
    public IList<string> AllowedWorkspaceIds { get; } = new List<string>();
}
