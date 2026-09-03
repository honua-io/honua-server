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
}
