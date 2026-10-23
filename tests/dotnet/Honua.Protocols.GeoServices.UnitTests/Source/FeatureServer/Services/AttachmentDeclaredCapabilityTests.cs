// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Validation;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class AttachmentDeclaredCapabilityTests
{
    [UnitTest]
    public void Write_WhenOnlyQueryIsDeclared_RefusesTheMappedOperation()
    {
        var denial = AttachmentEndpoints.DescribeDeniedAttachmentAccess(
            new MetadataV2Service(),
            new MetadataV2Publication { Capabilities = ["Query"] },
            AttachmentLayer(),
            AccessScope.Write,
            AuthorizationOperation.Insert);

        denial.Should().Be("Create edits are not enabled for this service");
    }

    [UnitTest]
    public void Write_WhenNoCapabilitiesAreDeclared_RefusesLikeApplyEdits()
    {
        var denial = AttachmentEndpoints.DescribeDeniedAttachmentAccess(
            new MetadataV2Service(),
            new MetadataV2Publication(),
            AttachmentLayer(),
            AccessScope.Write,
            AuthorizationOperation.Delete);

        denial.Should().Be("Delete edits are not enabled for this service");
    }

    [UnitTest]
    public void Write_WhenCapabilityIsDeclared_AllowsThatOperationOnly()
    {
        var publication = new MetadataV2Publication { Capabilities = ["Query", "Update"] };

        AttachmentEndpoints.DescribeDeniedAttachmentAccess(
                new MetadataV2Service(),
                publication,
                AttachmentLayer(),
                AccessScope.Write,
                AuthorizationOperation.Update)
            .Should().BeNull();

        AttachmentEndpoints.DescribeDeniedAttachmentAccess(
                new MetadataV2Service(),
                publication,
                AttachmentLayer(),
                AccessScope.Write,
                AuthorizationOperation.Insert)
            .Should().Be("Create edits are not enabled for this service");
    }

    [UnitTest]
    public void Read_WhenAttachmentsAreSupported_AllowsQueryOnlyMetadata()
    {
        AttachmentEndpoints.DescribeDeniedAttachmentAccess(
                new MetadataV2Service(),
                new MetadataV2Publication { Capabilities = ["Query"] },
                AttachmentLayer(),
                AccessScope.Read,
                writeOperation: null)
            .Should().BeNull();
    }

    [UnitTest]
    public void AnyScope_WhenLayerDoesNotSupportAttachments_Refuses()
    {
        var resource = new MetadataV2Resource
        {
            Editing = new MetadataV2ResourceEditing { SupportsAttachments = false }
        };

        AttachmentEndpoints.DescribeDeniedAttachmentAccess(
                new MetadataV2Service(),
                new MetadataV2Publication { Capabilities = ["Query", "Create", "Update", "Delete"] },
                resource,
                AccessScope.Read,
                writeOperation: null)
            .Should().Be("Attachments are not supported for this layer.");

        AttachmentEndpoints.DescribeDeniedAttachmentAccess(
                ServiceCapabilities("Create"),
                new MetadataV2Publication(),
                resource,
                AccessScope.Write,
                AuthorizationOperation.Insert)
            .Should().Be("Attachments are not supported for this layer.");
    }

    [UnitTest]
    public void Write_WhenServiceOptionsDeclareTheCapability_AllowsIt()
    {
        AttachmentEndpoints.DescribeDeniedAttachmentAccess(
                ServiceCapabilities("Query", "Editing"),
                new MetadataV2Publication(),
                AttachmentLayer(),
                AccessScope.Write,
                AuthorizationOperation.Update)
            .Should().BeNull();
    }

    private static MetadataV2Resource AttachmentLayer()
        => new()
        {
            Editing = new MetadataV2ResourceEditing { SupportsAttachments = true }
        };

    private static MetadataV2Service ServiceCapabilities(params string[] capabilities)
        => new()
        {
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(capabilities)
            }
        };
}
