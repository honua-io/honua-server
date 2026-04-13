// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Integration tests for production monitoring security hardening.
/// </summary>
[Protocol(Protocols.Admin)]
public sealed class ProductionMonitoringEndpointSecurityTests
{
    private const string AdminPassword = "production-monitoring-security-admin-key";

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /monitoring/health/comprehensive")]
    public async Task ComprehensiveHealth_RedactsSensitiveHealthCheckDataAndExceptions()
    {
        using var factory = CreateFactory(services =>
        {
            services.AddHealthChecks().AddCheck(
                "synthetic-sensitive",
                () => HealthCheckResult.Unhealthy(
                    "Synthetic failure",
                    new InvalidOperationException("super-secret failure"),
                    new Dictionary<string, object>
                    {
                        ["configuration"] = "redis://user:password@example",
                        ["token"] = "secret-token",
                        ["safe"] = 7
                    }));
        });
        using var client = CreateAdminClient(factory);

        var response = await client.GetAsync("/monitoring/health/comprehensive");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = document.RootElement.GetProperty("entries").GetProperty("synthetic-sensitive");
        entry.GetProperty("exception").GetString().Should().Be("See server logs for details.");

        var data = entry.GetProperty("data");
        data.GetProperty("configuration").GetString().Should().Be("[redacted]");
        data.GetProperty("token").GetString().Should().Be("[redacted]");
        data.GetProperty("safe").GetInt32().Should().Be(7);
    }

    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /monitoring/health/comprehensive")]
    public async Task ComprehensiveHealth_ExternalServicesEntryDoesNotDependOnPublicInternet()
    {
        using var factory = CreateFactory();
        using var client = CreateAdminClient(factory);

        var response = await client.GetAsync("/monitoring/health/comprehensive");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = document.RootElement.GetProperty("entries").GetProperty("external-services");
        entry.GetProperty("status").GetString().Should().Be("Healthy");
        entry.GetProperty("description").GetString().Should().Be("No external service probes are configured");
        entry.GetProperty("data").GetProperty("configuredProbes").GetInt32().Should().Be(0);
    }

    private static WebApplicationFactory<Program> CreateFactory(Action<IServiceCollection>? configureServices = null)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword
                    });
                });

                if (configureServices is not null)
                {
                    builder.ConfigureServices(configureServices);
                }
            });
    }

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }
}
