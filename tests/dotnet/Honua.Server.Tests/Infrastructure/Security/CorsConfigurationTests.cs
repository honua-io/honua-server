// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Infrastructure.Security;

[Trait("Category", "Unit")]
[Trait("Component", "Security")]
[Trait("Feature", "Cors")]
public sealed class CorsConfigurationTests
{
    [Fact]
    public async Task DevelopmentPolicy_ExposesPMTilesRangeProxyResponseHeaders()
    {
        // The PMTiles RangeProxy strategy (#845) requires browser clients to read
        // Accept-Ranges, Content-Range, Content-Length, ETag, and Last-Modified
        // off the proxy response. AllowAnyHeader on the development policy only
        // covers request headers, so exposed response headers must still be
        // enumerated explicitly to mirror the production policy.
        var policy = await ResolvePolicyAsync(
            CorsConfiguration.DevelopmentPolicy,
            isDevelopment: true,
            new Dictionary<string, string?>
            {
                ["Cors:DevelopmentOrigins:0"] = "http://localhost:3000"
            });

        Assert.Contains("Accept-Ranges", policy.ExposedHeaders);
        Assert.Contains("Content-Range", policy.ExposedHeaders);
        Assert.Contains("Content-Length", policy.ExposedHeaders);
        Assert.Contains("ETag", policy.ExposedHeaders);
        Assert.Contains("Last-Modified", policy.ExposedHeaders);
    }

    [Fact]
    public async Task ProductionPolicy_ExposesPMTilesRangeProxyResponseHeaders()
    {
        var policy = await ResolvePolicyAsync(
            CorsConfiguration.ProductionPolicy,
            isDevelopment: false,
            new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://app.example.com"
            });

        Assert.Contains("Accept-Ranges", policy.ExposedHeaders);
        Assert.Contains("Content-Range", policy.ExposedHeaders);
        Assert.Contains("Content-Length", policy.ExposedHeaders);
        Assert.Contains("ETag", policy.ExposedHeaders);
        Assert.Contains("Last-Modified", policy.ExposedHeaders);
    }

    private static async Task<CorsPolicy> ResolvePolicyAsync(
        string policyName,
        bool isDevelopment,
        IDictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var environment = new TestWebHostEnvironment
        {
            EnvironmentName = isDevelopment ? Environments.Development : Environments.Production
        };

        var services = new ServiceCollection();
        services.AddCorsPolicies(configuration, environment);
        var provider = services.BuildServiceProvider();

        var policyProvider = provider.GetRequiredService<ICorsPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(new DefaultHttpContext(), policyName);
        Assert.NotNull(policy);
        return policy!;
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Honua.Server.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
