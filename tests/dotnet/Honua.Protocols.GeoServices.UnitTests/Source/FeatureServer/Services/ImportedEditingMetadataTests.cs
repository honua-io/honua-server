// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class ImportedEditingMetadataTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void AttachmentSupport_UsesCanonicalEditingBeforeLegacyAnnotations(bool? canonical, bool legacy, bool expected)
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new() { Annotations = new Dictionary<string, string> { ["honua.io/attachments"] = legacy.ToString() } },
            Editing = canonical.HasValue ? new() { SupportsAttachments = canonical.Value } : null
        };
        FeatureServerEndpoints.ResourceSupportsAttachmentsV2(resource).Should().Be(expected);
    }

    [Fact]
    public void GlobalIdField_IsDistinctFromOrdinaryUuidAndCannotBeEdited()
    {
        var field = new MetadataV2Field { Name = "stable_id", Type = MetadataV2FieldType.Uuid };
        var globalId = FeatureServerEndpoints.MapFieldInfoV2(field, "objectid", "STABLE_ID");
        globalId.Type.Should().Be("esriFieldTypeGlobalID");
        globalId.Editable.Should().BeFalse();
        FeatureServerEndpoints.MapFieldInfoV2(field, "objectid").Type.Should().Be("esriFieldTypeGUID");
    }

    [Theory]
    [InlineData(false, "stable_id")]
    [InlineData(true, null)]
    public void GlobalIdBinding_OnlyAdvertisesVisibleUuidFields(bool hidden, string? expected)
    {
        var resource = new MetadataV2Resource
        {
            Editing = new() { GlobalIdField = "STABLE_ID" },
            SchemaFields = [new() { Name = "stable_id", Type = MetadataV2FieldType.Uuid, Hidden = hidden }]
        };
        FeatureServerEndpoints.ResolveGlobalIdFieldV2(resource).Should().Be(expected);
    }
}
