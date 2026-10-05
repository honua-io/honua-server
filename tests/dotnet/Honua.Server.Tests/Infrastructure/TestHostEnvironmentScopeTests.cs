// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
public sealed class TestHostEnvironmentScopeTests
{
    [Fact]
    public void TestWebApplicationFactory_ExposesParameterlessClassFixtureConstructor()
    {
        var publicConstructors = typeof(TestWebApplicationFactory).GetConstructors();

        publicConstructors.Should().ContainSingle(
            "xUnit requires class fixtures to expose exactly one public constructor");
        publicConstructors[0].GetParameters().Should().BeEmpty(
            "xUnit must construct IClassFixture<TestWebApplicationFactory> without resolving an environment-name fixture");
    }

    [Fact]
    public void WebAppFixture_TestEnvironmentSettings_AreScopedAndRestored()
    {
        const string dotnetKey = "DOTNET_ENVIRONMENT";
        const string aspnetKey = "ASPNETCORE_ENVIRONMENT";
        const string schemaHeadersKey = "HONUA_TEST_SCHEMA_HEADERS";
        var originalDotnet = Environment.GetEnvironmentVariable(dotnetKey);
        var originalAspnet = Environment.GetEnvironmentVariable(aspnetKey);
        var originalSchemaHeaders = Environment.GetEnvironmentVariable(schemaHeadersKey);

        try
        {
            Environment.SetEnvironmentVariable(dotnetKey, "Production");
            Environment.SetEnvironmentVariable(aspnetKey, "Staging");
            Environment.SetEnvironmentVariable(schemaHeadersKey, "false");

            _ = new WebAppFixture();
            Environment.GetEnvironmentVariable(dotnetKey).Should().Be("Production");
            Environment.GetEnvironmentVariable(aspnetKey).Should().Be("Staging");
            Environment.GetEnvironmentVariable(schemaHeadersKey).Should().Be("false");

            _ = ConfiguredWebApplicationFactory.StartInTestEnvironment(() =>
            {
                Environment.GetEnvironmentVariable(dotnetKey).Should().Be("Test");
                Environment.GetEnvironmentVariable(aspnetKey).Should().Be("Test");
                Environment.GetEnvironmentVariable(schemaHeadersKey).Should().Be("true");
                return Substitute.For<IHost>();
            });

            Environment.GetEnvironmentVariable(dotnetKey).Should().Be("Production");
            Environment.GetEnvironmentVariable(aspnetKey).Should().Be("Staging");
            Environment.GetEnvironmentVariable(schemaHeadersKey).Should().Be("false");
        }
        finally
        {
            Environment.SetEnvironmentVariable(dotnetKey, originalDotnet);
            Environment.SetEnvironmentVariable(aspnetKey, originalAspnet);
            Environment.SetEnvironmentVariable(schemaHeadersKey, originalSchemaHeaders);
        }
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public void ConfiguredFactory_ProcessEnvironmentHeldByAnotherHost_DoesNotLeakIntoTheRequestedEnvironment()
    {
        // WebApplicationFactory builds the web host builder, which snapshots ASPNETCORE_*,
        // before CreateHost takes the environment gate. A concurrent host that is inside
        // StartInEnvironment at that instant holds ASPNETCORE_ENVIRONMENT; an unpinned host
        // inherited it and booted without the provider composition (#4640).
        const string aspnetKey = "ASPNETCORE_ENVIRONMENT";
        var originalAspnet = Environment.GetEnvironmentVariable(aspnetKey);
        string? environmentSeenByConfigure = null;

        try
        {
            Environment.SetEnvironmentVariable(aspnetKey, "Staging");

            using var factory = ConfiguredWebApplicationFactory.Create(
                builder =>
                {
                    environmentSeenByConfigure = builder.GetSetting(WebHostDefaults.EnvironmentKey);
                    TestWebApplicationFactory.ConfigureForTests(builder);
                },
                "Test");
            var environment = factory.Services.GetRequiredService<IHostEnvironment>();

            environmentSeenByConfigure.Should().Be(
                "Test",
                "the factory pins its requested environment before the caller configures the builder");
            environment.EnvironmentName.Should().Be("Test");
        }
        finally
        {
            Environment.SetEnvironmentVariable(aspnetKey, originalAspnet);
        }
    }

    [Fact]
    public void StartInEnvironment_RequestedEnvironmentIsVisibleDuringStartupAndRestored()
    {
        const string dotnetKey = "DOTNET_ENVIRONMENT";
        const string aspnetKey = "ASPNETCORE_ENVIRONMENT";
        const string schemaHeadersKey = "HONUA_TEST_SCHEMA_HEADERS";
        var originalDotnet = Environment.GetEnvironmentVariable(dotnetKey);
        var originalAspnet = Environment.GetEnvironmentVariable(aspnetKey);
        var originalSchemaHeaders = Environment.GetEnvironmentVariable(schemaHeadersKey);

        try
        {
            Environment.SetEnvironmentVariable(dotnetKey, "Test");
            Environment.SetEnvironmentVariable(aspnetKey, "Test");
            Environment.SetEnvironmentVariable(schemaHeadersKey, "true");

            _ = ConfiguredWebApplicationFactory.StartInEnvironment(
                () =>
                {
                    Environment.GetEnvironmentVariable(dotnetKey).Should().Be("Production");
                    Environment.GetEnvironmentVariable(aspnetKey).Should().Be("Production");
                    Environment.GetEnvironmentVariable(schemaHeadersKey).Should().BeNull();
                    return Substitute.For<IHost>();
                },
                "Production",
                enableTestSchemaHeaders: false);

            Environment.GetEnvironmentVariable(dotnetKey).Should().Be("Test");
            Environment.GetEnvironmentVariable(aspnetKey).Should().Be("Test");
            Environment.GetEnvironmentVariable(schemaHeadersKey).Should().Be("true");
        }
        finally
        {
            Environment.SetEnvironmentVariable(dotnetKey, originalDotnet);
            Environment.SetEnvironmentVariable(aspnetKey, originalAspnet);
            Environment.SetEnvironmentVariable(schemaHeadersKey, originalSchemaHeaders);
        }
    }
}
