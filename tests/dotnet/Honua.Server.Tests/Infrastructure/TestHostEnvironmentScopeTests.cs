// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure;

[Collection("Database")]
public sealed class TestHostEnvironmentScopeTests
{
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
}
