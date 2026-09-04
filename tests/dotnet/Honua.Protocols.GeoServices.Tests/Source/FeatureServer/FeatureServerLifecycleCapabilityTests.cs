// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerLifecycleCapabilityTests
{
    private static readonly string[] DeclaredCapabilities = ["Query", "Sync"];
    private static readonly string[] UpdateOnlyCapabilities = ["Query", "Update"];
    private static readonly string[] AllEditCapabilities = ["Query", "Create", "Update", "Delete"];

    [Fact]
    public void BuildServiceCapabilities_WhenOfflineSyncDisabled_OmitsDeclaredSync()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "sync-service", Name = "Sync Service" },
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(DeclaredCapabilities)
            }
        };

        FeatureServerEndpoints.BuildServiceCapabilitiesV2(service, offlineSyncEnabled: false)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("Query");

        FeatureServerEndpoints.BuildServiceCapabilitiesV2(service, offlineSyncEnabled: true)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain("Sync");
    }

    [Fact]
    public void BuildServiceCapabilities_EmitsOnlyDeclaredEditOperations()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "update-only", Name = "Update Only" },
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(UpdateOnlyCapabilities)
            }
        };

        FeatureServerEndpoints.BuildServiceCapabilitiesV2(service)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("Query", "Update", "Editing");
        FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Create").Should().BeFalse();
        FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Update").Should().BeTrue();
        FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Delete").Should().BeFalse();
    }

    [Fact]
    public void BuildServiceCapabilities_UsesValidatedPublicationCapabilitiesWhenPresent()
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "Service" },
            Options = new Dictionary<string, JsonElement>
            {
                ["capabilities"] = JsonSerializer.SerializeToElement(AllEditCapabilities)
            }
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication", Name = "Publication" },
            Capabilities = ["Query", "Update"]
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "Resource" }
        };

        FeatureServerEndpoints.BuildServiceCapabilitiesV2(service, [(publication, resource)])
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("Query", "Update", "Editing");
        FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Create", publication).Should().BeFalse();
        FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Update", publication).Should().BeTrue();
        FeatureServerEndpoints.ServiceSupportsOperationV2(service, "Delete", publication).Should().BeFalse();
    }
}
