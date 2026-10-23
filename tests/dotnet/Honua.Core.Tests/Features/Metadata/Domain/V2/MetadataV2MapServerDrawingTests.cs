// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

public sealed class MetadataV2MapServerDrawingTests
{
    [UnitTheory]
    [InlineData(null, true)]
    [InlineData("\"cached\"", true)]
    [InlineData("\"dynamic\"", false)]
    [InlineData("\"DYNAMIC\"", false)]
    public void ValidModes_ResolveAndPassGraphValidation(string? json, bool cached)
    {
        var service = CreateService(json);
        MetadataV2MapServerDrawing.TryResolveCachedDrawing(service, out var resolved).Should().BeTrue();
        resolved.Should().Be(cached);
        MetadataV2MapServerDrawing.UsesCachedDrawing(service).Should().Be(cached);
        MetadataV2GraphValidator.Validate(new MetadataV2Graph { Services = [service] }).IsValid.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("0")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    [InlineData("\"auto\"")]
    [InlineData("\" dynamic \"")]
    public void InvalidExplicitModes_FailMetadataValidationAndCannotFallBackToCached(string json)
    {
        var service = CreateService(json);
        MetadataV2MapServerDrawing.TryResolveCachedDrawing(service, out _).Should().BeFalse();
        var result = MetadataV2GraphValidator.Validate(new MetadataV2Graph { Services = [service] });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("options.mapServerDrawingMode", StringComparison.Ordinal));
        var resolve = () => MetadataV2MapServerDrawing.UsesCachedDrawing(service);
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*mapServerDrawingMode*");
    }

    private static MetadataV2Service CreateService(string? json)
    {
        var options = new Dictionary<string, JsonElement>();
        if (json is not null)
        {
            using var document = JsonDocument.Parse(json);
            options.Add(MetadataV2MapServerDrawing.OptionName, document.RootElement.Clone());
        }
        return new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "map", Name = "Map" },
            Protocols = [ServiceProtocols.MapServer],
            Options = options
        };
    }
}
