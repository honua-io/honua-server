// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

public sealed class OgcProcessesCiteEchoFixtureConfigurationTests
{
    [UnitTest]
    public void IsEnabled_RequiresTheActualTestHostEnvironment()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Test",
                ["HONUA_REGISTER_TEST_INFRASTRUCTURE"] = "true",
                ["OgcProcesses:CertificationProfile"] = "ogcapi-processes10"
            })
            .Build();

        OgcProcessesCiteEchoFixture.IsEnabled(configuration, "Production")
            .Should().BeFalse("an application setting must not spoof the actual host environment");
        OgcProcessesCiteEchoFixture.IsEnabled(configuration, "Test")
            .Should().BeTrue();
    }

    [UnitTest]
    public void IsEnabled_RequiresEveryCertificationGate()
    {
        var missingInfrastructureOptIn = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OgcProcesses:CertificationProfile"] = "ogcapi-processes10"
            })
            .Build();
        var wrongProfile = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_REGISTER_TEST_INFRASTRUCTURE"] = "true",
                ["OgcProcesses:CertificationProfile"] = "other"
            })
            .Build();

        OgcProcessesCiteEchoFixture.IsEnabled(missingInfrastructureOptIn, "Test")
            .Should().BeFalse();
        OgcProcessesCiteEchoFixture.IsEnabled(wrongProfile, "Test")
            .Should().BeFalse();
    }

    [UnitTest]
    public void TryDecodeArtifact_RejectsPayloadBeyondConfiguredLimit()
    {
        var oversized = OgcProcessesCiteEchoFixture.DataUriPrefix
            + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new string('x', 64)));

        OgcProcessesCiteEchoExecutor.TryDecodeArtifact(oversized, 16, out _)
            .Should().BeFalse();
    }

    [UnitTest]
    public void TryAddOutputBindings_RejectsUnknownAndReferenceOutputs()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknown = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["unknown"] = JsonSerializer.SerializeToElement(new { transmissionMode = "value" })
        };
        var reference = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["literal"] = JsonSerializer.SerializeToElement(new { transmissionMode = "reference" })
        };
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["literal"] = JsonSerializer.SerializeToElement("teststring")
        };

        OgcProcessesCiteEchoFixture.TryAddOutputBindings(metadata, inputs, unknown, out _)
            .Should().BeFalse();
        OgcProcessesCiteEchoFixture.TryAddOutputBindings(metadata, inputs, reference, out _)
            .Should().BeFalse();
        metadata.Should().BeEmpty();
    }

    [UnitTest]
    public void TryAddOutputBindings_SelectsOnlyOutputsBackedBySubmittedInputs()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["literal"] = JsonSerializer.SerializeToElement("teststring"),
            ["array"] = JsonSerializer.SerializeToElement("teststring"),
            ["pause"] = JsonSerializer.SerializeToElement(1)
        };

        OgcProcessesCiteEchoFixture.TryAddOutputBindings(metadata, inputs, null, out _)
            .Should().BeTrue();

        metadata.Should().Equal(
            new KeyValuePair<string, string>("process.output.0", "literal"),
            new KeyValuePair<string, string>("process.output.1", "array"));
    }
}
