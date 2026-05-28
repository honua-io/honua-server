// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Metadata v2 authorization action identifier.
/// </summary>
public readonly record struct MetadataV2PolicyAction(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Known metadata v2 authorization actions.
/// </summary>
public static class MetadataV2PolicyActions
{
    /// <summary>
    /// Read metadata resources and resolved projections.
    /// </summary>
    public static readonly MetadataV2PolicyAction MetadataRead = new("metadata.read");

    /// <summary>
    /// Create or update metadata resources.
    /// </summary>
    public static readonly MetadataV2PolicyAction MetadataWrite = new("metadata.write");

    /// <summary>
    /// Query feature payloads through metadata-backed services.
    /// </summary>
    public static readonly MetadataV2PolicyAction FeaturesQuery = new("features.query");

    /// <summary>
    /// Edit feature payloads through metadata-backed services.
    /// </summary>
    public static readonly MetadataV2PolicyAction FeaturesEdit = new("features.edit");

    /// <summary>
    /// Read vector tile payloads.
    /// </summary>
    public static readonly MetadataV2PolicyAction TilesRead = new("tiles.read");

    /// <summary>
    /// Render raster payloads.
    /// </summary>
    public static readonly MetadataV2PolicyAction RasterRender = new("raster.render");

    /// <summary>
    /// Publish catalog entries from approved metadata.
    /// </summary>
    public static readonly MetadataV2PolicyAction CatalogPublish = new("catalog.publish");

    /// <summary>
    /// Manage metadata connection definitions and references.
    /// </summary>
    public static readonly MetadataV2PolicyAction ConnectionsManage = new("connections.manage");

    /// <summary>
    /// Write RBAC policy configuration.
    /// </summary>
    public static readonly MetadataV2PolicyAction AdminRbacWrite = new("admin.rbac.write");

    /// <summary>
    /// All known metadata v2 authorization actions.
    /// </summary>
    public static IReadOnlyList<MetadataV2PolicyAction> All { get; } =
    [
        MetadataRead,
        MetadataWrite,
        FeaturesQuery,
        FeaturesEdit,
        TilesRead,
        RasterRender,
        CatalogPublish,
        ConnectionsManage,
        AdminRbacWrite
    ];
}

/// <summary>
/// Named metadata v2 policy preset.
/// </summary>
public sealed record MetadataV2PolicyPreset(string Name, IReadOnlyList<MetadataV2PolicyAction> Actions)
{
    /// <summary>
    /// Indicates whether the preset includes the supplied action.
    /// </summary>
    public bool Allows(MetadataV2PolicyAction action) => Actions.Contains(action);
}

/// <summary>
/// Built-in metadata v2 policy presets.
/// </summary>
public static class MetadataV2PolicyPresets
{
    /// <summary>
    /// Read-only catalog and data access.
    /// </summary>
    public static MetadataV2PolicyPreset Reader { get; } = new(
        "metadata.reader",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.FeaturesQuery,
            MetadataV2PolicyActions.TilesRead,
            MetadataV2PolicyActions.RasterRender
        ]);

    /// <summary>
    /// Metadata and feature editing access.
    /// </summary>
    public static MetadataV2PolicyPreset Editor { get; } = new(
        "metadata.editor",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.MetadataWrite,
            MetadataV2PolicyActions.FeaturesQuery,
            MetadataV2PolicyActions.FeaturesEdit,
            MetadataV2PolicyActions.TilesRead,
            MetadataV2PolicyActions.RasterRender
        ]);

    /// <summary>
    /// Catalog publishing access.
    /// </summary>
    public static MetadataV2PolicyPreset Publisher { get; } = new(
        "metadata.publisher",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.MetadataWrite,
            MetadataV2PolicyActions.FeaturesQuery,
            MetadataV2PolicyActions.FeaturesEdit,
            MetadataV2PolicyActions.TilesRead,
            MetadataV2PolicyActions.RasterRender,
            MetadataV2PolicyActions.CatalogPublish
        ]);

    /// <summary>
    /// Connection reference administration access.
    /// </summary>
    public static MetadataV2PolicyPreset ConnectionManager { get; } = new(
        "metadata.connection-manager",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.ConnectionsManage
        ]);

    /// <summary>
    /// RBAC administration access for metadata policies.
    /// </summary>
    public static MetadataV2PolicyPreset RbacAdministrator { get; } = new(
        "metadata.rbac-administrator",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.AdminRbacWrite
        ]);

    /// <summary>
    /// All built-in metadata v2 policy presets.
    /// </summary>
    public static IReadOnlyList<MetadataV2PolicyPreset> All { get; } =
    [
        Reader,
        Editor,
        Publisher,
        ConnectionManager,
        RbacAdministrator
    ];
}
