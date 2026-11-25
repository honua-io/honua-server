// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ConfigurationDiscoveryServiceAotTests
{
    private enum SampleMode
    {
        Standard = 0,
        Strict = 1
    }

    private sealed class NestedSampleOptions
    {
        public string Label { get; set; } = "nested";
    }

    [UnitTest]
    public void RedactAndNormalizeValue_ConvertsNonPrimitiveValuesToAotSerializablePrimitives()
    {
        var result = CreateSampleDiscoveryResult();

        var json = JsonSerializer.Serialize(result, ConfigurationDiscoveryJsonContext.Default.ConfigurationDiscoveryResult);
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.GetProperty("discoveredTypes")[0].GetProperty("currentValues");

        values.GetProperty("Mode").GetString().Should().Be("Strict");
        values.GetProperty("Nested").GetString().Should().Be(nameof(NestedSampleOptions));
        values.GetProperty("Endpoint").GetString().Should().Be("https://example.test/api");
        values.GetProperty("Id").GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        values.GetProperty("Tags").EnumerateArray().Select(e => e.GetString()).Should().Equal("alpha", "beta");
        values.GetProperty("ApiPassword").GetString().Should().Be(ConfigurationSecretRedactor.Mask);
        values.GetProperty("Count").GetInt32().Should().Be(42);
        json.Should().NotContain("super-secret-value");
    }

    private static ConfigurationDiscoveryResult CreateSampleDiscoveryResult() => new()
    {
        DiscoveredTypes =
        [
            new ConfigurationTypeInfo
            {
                SectionName = "Sample",
                TypeName = "SampleOptions",
                FullTypeName = "Sample.SampleOptions",
                Assembly = "Honua.Server",
                IsRegisteredInDi = true,
                CurrentValues = new Dictionary<string, object?>
                {
                    ["Mode"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("Mode", SampleMode.Strict),
                    ["Nested"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("Nested", new NestedSampleOptions()),
                    ["Endpoint"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("BaseUri", new Uri("https://example.test/api")),
                    ["Id"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("Id", Guid.Parse("11111111-1111-1111-1111-111111111111")),
                    ["Timestamp"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("Timestamp", DateTimeOffset.UnixEpoch),
                    ["Tags"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("Tags", new List<string> { "alpha", "beta" }),
                    ["ApiPassword"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("ApiPassword", "super-secret-value"),
                    ["Count"] = ConfigurationDiscoveryService.RedactAndNormalizeValue("Count", 42),
                }
            }
        ]
    };
}
