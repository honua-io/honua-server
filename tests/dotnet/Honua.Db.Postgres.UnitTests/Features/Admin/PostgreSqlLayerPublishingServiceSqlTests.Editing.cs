// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed partial class PostgreSqlLayerPublishingServiceSqlTests
{
    [global::Xunit.Trait("Tier", "Fast")]
    [Theory]
    [InlineData(LayerStorageMode.Managed, "attachments", "editable")]
    [InlineData(LayerStorageMode.Managed, "globalId", "editable")]
    [InlineData(LayerStorageMode.Managed, "both", "editing-token")]
    [InlineData(LayerStorageMode.Managed, "attachments", "read-only")]
    [InlineData(LayerStorageMode.Managed, "attachments", "default")]
    [InlineData(LayerStorageMode.Source, "attachments", "default")]
    [InlineData(LayerStorageMode.Source, "globalId", "default")]
    [InlineData(LayerStorageMode.Source, "copy", "default")]
    [InlineData(LayerStorageMode.Source, "copy", "read-only")]
    public void ResolveEditingForPublish_SetsCanModifyOnlyWhenManagedAndEditable(
        LayerStorageMode storageMode,
        string mode,
        string capabilityProfile)
    {
        var request = new LayerPublishRequest
        {
            Schema = "public",
            Table = "source",
            LayerName = "Layer",
            StorageMode = storageMode,
            SupportsAttachments = mode is "attachments" or "both",
            GlobalIdField = mode is "globalId" or "both" ? "stable_id" : null,
            CreateEditableCopy = mode == "copy",
            Capabilities = capabilityProfile switch
            {
                "editable" => ["Query", "Create", "Update", "Delete"],
                "editing-token" => ["Query", "Editing"],
                "read-only" => ["Query"],
                _ => null
            }
        };
        var editing = InvokeResolveEditingForPublish(request);
        var expectedCanModify = (storageMode == LayerStorageMode.Managed && capabilityProfile is "editable" or "editing-token")
            || (mode == "copy" && capabilityProfile == "default");

        editing.Should().NotBeNull();
        editing!.CanModify.Should().Be(expectedCanModify);
        editing.SupportsAttachments.Should().Be(request.SupportsAttachments);
        editing.GlobalIdField.Should().Be(request.GlobalIdField);
    }

    private static MetadataV2ObjectEditing? InvokeResolveEditingForPublish(LayerPublishRequest request)
    {
        var serviceType = typeof(PostgreSqlLayerPublishingService);
        var fieldType = serviceType.GetNestedType("LayerFieldInsert", BindingFlags.NonPublic)!;
        var fields = Array.CreateInstance(fieldType, 2);
        fields.SetValue(Activator.CreateInstance(fieldType,
            ["objectid", MetadataV2FieldType.Integer, null, false, null, null, null]), 0);
        fields.SetValue(Activator.CreateInstance(fieldType,
            ["stable_id", MetadataV2FieldType.Uuid, null, false, null, null, null]), 1);
        var method = serviceType.GetMethod("ResolveEditingForPublish", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (MetadataV2ObjectEditing?)method.Invoke(null, [request, fields]);
    }
}
