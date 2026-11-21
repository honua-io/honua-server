// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2WmsProtectedLayersTests
{
    [UnitTheory]
    [InlineData(null, MetadataV2WmsProtectedLayerMode.Hide)]
    [InlineData("\"hide\"", MetadataV2WmsProtectedLayerMode.Hide)]
    [InlineData("\"challenge\"", MetadataV2WmsProtectedLayerMode.Challenge)]
    [InlineData("\"CHALLENGE\"", MetadataV2WmsProtectedLayerMode.Challenge)]
    public void ValidModes_ResolveAndPassGraphValidation(string? json, MetadataV2WmsProtectedLayerMode mode)
    {
        var service = CreateService(json);
        MetadataV2WmsProtectedLayers.ResolveMode(service).Should().Be(mode);
        MetadataV2WmsProtectedLayers.ChallengesProtectedLayers(service)
            .Should().Be(mode == MetadataV2WmsProtectedLayerMode.Challenge);
        MetadataV2GraphValidator.Validate(new MetadataV2Graph { Services = [service] }).IsValid.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("\"list\"")]
    [InlineData("\" challenge \"")]
    public void InvalidModes_FailValidationAndKeepLayersHidden(string json)
    {
        var service = CreateService(json);
        MetadataV2WmsProtectedLayers.ResolveMode(service).Should().BeNull();
        MetadataV2WmsProtectedLayers.ChallengesProtectedLayers(service).Should().BeFalse();
        var result = MetadataV2GraphValidator.Validate(new MetadataV2Graph { Services = [service] });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("options.wmsProtectedLayers", StringComparison.Ordinal));
    }

    private static MetadataV2Service CreateService(string? json)
    {
        var options = new Dictionary<string, JsonElement>();
        if (json is not null)
        {
            using var document = JsonDocument.Parse(json);
            options.Add(MetadataV2WmsProtectedLayers.OptionName, document.RootElement.Clone());
        }
        return new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "map", Name = "Map" },
            Protocols = [ServiceProtocols.MapServer],
            Options = options
        };
    }
}
