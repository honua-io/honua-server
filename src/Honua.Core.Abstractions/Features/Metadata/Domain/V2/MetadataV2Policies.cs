// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public readonly record struct MetadataV2PolicyAction(string Value)
{
    public override string ToString() => Value;
}

public static class MetadataV2PolicyActions
{
    public static readonly MetadataV2PolicyAction MetadataRead = new("metadata.read");

    public static readonly MetadataV2PolicyAction MetadataWrite = new("metadata.write");

    public static readonly MetadataV2PolicyAction FeaturesQuery = new("features.query");

    public static readonly MetadataV2PolicyAction FeaturesEdit = new("features.edit");

    public static readonly MetadataV2PolicyAction TilesRead = new("tiles.read");

    public static readonly MetadataV2PolicyAction RasterRender = new("raster.render");

    public static readonly MetadataV2PolicyAction CatalogPublish = new("catalog.publish");

    public static readonly MetadataV2PolicyAction ConnectionsManage = new("connections.manage");

    public static readonly MetadataV2PolicyAction AdminRbacWrite = new("admin.rbac.write");

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

public sealed record MetadataV2PolicyPreset(string Name, IReadOnlyList<MetadataV2PolicyAction> Actions)
{
    public bool Allows(MetadataV2PolicyAction action) => Actions.Contains(action);
}

public static class MetadataV2PolicyPresets
{
    public static MetadataV2PolicyPreset Reader { get; } = new(
        "metadata.reader",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.FeaturesQuery,
            MetadataV2PolicyActions.TilesRead,
            MetadataV2PolicyActions.RasterRender
        ]);

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

    public static MetadataV2PolicyPreset ConnectionManager { get; } = new(
        "metadata.connection-manager",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.ConnectionsManage
        ]);

    public static MetadataV2PolicyPreset RbacAdministrator { get; } = new(
        "metadata.rbac-administrator",
        [
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.AdminRbacWrite
        ]);

    public static IReadOnlyList<MetadataV2PolicyPreset> All { get; } =
    [
        Reader,
        Editor,
        Publisher,
        ConnectionManager,
        RbacAdministrator
    ];
}
