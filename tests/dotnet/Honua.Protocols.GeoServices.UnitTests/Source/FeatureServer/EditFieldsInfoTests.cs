// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>GeoServices editFieldsInfo projection from metadata editing bindings (#6134).</summary>
public sealed class EditFieldsInfoTests
{
    [UnitTest]
    public void LayerMetadata_ProjectsDeclaredEditorTrackingFields()
    {
        var resource = new MetadataV2Resource
        {
            Editing = new MetadataV2ResourceEditing
            {
                GlobalIdField = "GlobalID",
                CreatorField = "created_user",
                CreatedAtField = "created_date",
                EditorField = "editor",
                UpdatedAtField = "updated_date",
            },
        };
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph(), "test", DateTimeOffset.UnixEpoch);
        var layer = FeatureServerEndpoints.MapLayerToResponseV2(
            new MetadataV2Service(),
            resource,
            new MetadataV2Publication(),
            snapshot,
            new QueryLimits(),
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            offlineSyncEnabled: false);

        layer.EditFieldsInfo.Should().NotBeNull();
        layer.EditFieldsInfo!.CreatorField.Should().Be("created_user");
        layer.EditFieldsInfo.CreationDateField.Should().Be("created_date");
        layer.EditFieldsInfo.EditorField.Should().Be("editor");
        layer.EditFieldsInfo.EditDateField.Should().Be("updated_date");
    }

    [UnitTest]
    public void LayerMetadata_OmitsEditFieldsInfoWhenTrackingIsUnset()
    {
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph(), "test", DateTimeOffset.UnixEpoch);
        var layer = FeatureServerEndpoints.MapLayerToResponseV2(
            new MetadataV2Service(),
            new MetadataV2Resource(),
            new MetadataV2Publication(),
            snapshot,
            new QueryLimits(),
            null,
            null,
            null,
            null,
            false,
            false,
            false,
            offlineSyncEnabled: false);

        layer.EditFieldsInfo.Should().BeNull();
    }
}
