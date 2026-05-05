// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Metadata.Schema;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata;

/// <summary>
/// Tests for MapTemplate and Theme registration in the metadata schema registry.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class MetadataSchemaRegistryPackagingTests
{
    private readonly MetadataSchemaRegistry _registry = new();

    [UnitTest]
    [Operation(Operations.Query)]
    public void MetadataResourceKinds_All_ContainsMapTemplate()
    {
        MetadataResourceKinds.All.Should().Contain("MapTemplate");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void MetadataResourceKinds_All_ContainsTheme()
    {
        MetadataResourceKinds.All.Should().Contain("Theme");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void MetadataResourceKinds_All_ContainsGroup()
    {
        MetadataResourceKinds.All.Should().Contain("Group");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void MetadataResourceKinds_All_ContainsSourceDescriptor()
    {
        MetadataResourceKinds.All.Should().Contain("SourceDescriptor");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_MapTemplate_WithRequiredFields_Succeeds()
    {
        var resource = CreateResource("MapTemplate", """{"name":"Base Topo","category":"basemap"}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_MapTemplate_MissingName_Fails()
    {
        var resource = CreateResource("MapTemplate", """{"category":"basemap"}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("spec.name"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_MapTemplate_MissingCategory_Fails()
    {
        var resource = CreateResource("MapTemplate", """{"name":"Base Topo"}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("spec.category"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_Theme_WithRequiredFields_Succeeds()
    {
        var resource = CreateResource("Theme", """{"name":"Dark Mode"}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_Theme_MissingName_Fails()
    {
        var resource = CreateResource("Theme", """{"palette":"dark"}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("spec.name"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_Group_WithEmptySpec_Succeeds()
    {
        var resource = CreateResource("Group", """{}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_SourceDescriptor_WithRequiredFields_Succeeds()
    {
        var resource = CreateResource(
            "SourceDescriptor",
            """
            {
              "sourceDescriptor": {
                "id": "parks-source",
                "protocol": "geoservices-feature-service",
                "locator": {
                  "serviceId": "parks",
                  "layerId": 0
                },
                "capabilities": ["Query"]
              }
            }
            """);

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_SourceDescriptor_MissingProtocol_Fails()
    {
        var resource = CreateResource(
            "SourceDescriptor",
            """
            {
              "sourceDescriptor": {
                "id": "parks-source",
                "locator": {
                  "serviceId": "parks",
                  "layerId": 0
                }
              }
            }
            """);

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("spec.sourceDescriptor.protocol"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_SourceDescriptor_NonStringCapability_Fails()
    {
        var resource = CreateResource(
            "SourceDescriptor",
            """
            {
              "sourceDescriptor": {
                "id": "parks-source",
                "protocol": "geoservices-feature-service",
                "capabilities": ["Query", 42]
              }
            }
            """);

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("capabilities entries"));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_Style_StillValidates()
    {
        var resource = CreateResource("Style", """{"layerId":"layer-1","style":{"type":"fill"}}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void ValidateAndUpgrade_Connection_StillValidates()
    {
        var resource = CreateResource("Connection", """{"name":"prod-db","host":"db.example.com","databaseName":"honua"}""");

        var result = _registry.ValidateAndUpgrade(resource);

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void GetSupportedApiVersions_MapTemplate_ReturnsCurrentVersion()
    {
        var versions = _registry.GetSupportedApiVersions("MapTemplate");

        versions.Should().ContainSingle().Which.Should().Be(MetadataSchemaRegistry.CurrentVersion);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void GetSupportedApiVersions_Theme_ReturnsCurrentVersion()
    {
        var versions = _registry.GetSupportedApiVersions("Theme");

        versions.Should().ContainSingle().Which.Should().Be(MetadataSchemaRegistry.CurrentVersion);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void GetSupportedApiVersions_Group_ReturnsCurrentVersion()
    {
        var versions = _registry.GetSupportedApiVersions("Group");

        versions.Should().ContainSingle().Which.Should().Be(MetadataSchemaRegistry.CurrentVersion);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void GetSupportedApiVersions_SourceDescriptor_ReturnsCurrentVersion()
    {
        var versions = _registry.GetSupportedApiVersions("SourceDescriptor");

        versions.Should().ContainSingle().Which.Should().Be(MetadataSchemaRegistry.CurrentVersion);
    }

    private static MetadataResource CreateResource(string kind, string specJson)
    {
        return new MetadataResource
        {
            ApiVersion = MetadataSchemaRegistry.CurrentVersion,
            Kind = kind,
            Metadata = new ResourceMetadata { Name = $"test-{kind.ToLowerInvariant()}" },
            Spec = JsonDocument.Parse(specJson).RootElement.Clone()
        };
    }
}
