// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
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

    [Fact]
    public async Task ProductionPolicy_AllowsBrowserConsoleMcpSessionAndIdempotencyHeaders()
    {
        var policy = await ResolvePolicyAsync(
            CorsConfiguration.ProductionPolicy,
            isDevelopment: false,
            new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://console.example.com"
            });

        Assert.Contains("Mcp-Session-Id", policy.Headers, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Idempotency-Key", policy.Headers, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Mcp-Session-Id", policy.ExposedHeaders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DevelopmentPolicy_ExposesMcpSessionHeader()
    {
        var policy = await ResolvePolicyAsync(
            CorsConfiguration.DevelopmentPolicy,
            isDevelopment: true,
            new Dictionary<string, string?>
            {
                ["Cors:DevelopmentOrigins:0"] = "http://localhost:3000"
            });

        Assert.Contains("Mcp-Session-Id", policy.ExposedHeaders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductionPolicy_AllowsConditionalHeadersByDefault()
    {
        // If-Match (and other conditional headers) must be allowed cross-origin so
        // browser/Office clients can send ETag preconditions on PATCH/PUT/DELETE,
        // enabling optimistic concurrency from the browser (#1629).
        var policy = await ResolvePolicyAsync(
            CorsConfiguration.ProductionPolicy,
            isDevelopment: false,
            new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://app.example.com"
            });

        Assert.Contains("If-Match", policy.Headers);
        Assert.Contains("If-Unmodified-Since", policy.Headers);
        Assert.Contains("If-None-Match", policy.Headers);
    }

    [Fact]
    public async Task ProductionPolicy_AppendsConfiguredHeadersWithoutDroppingDefaults()
    {
        // Operator-supplied Cors:AllowedHeaders entries extend the default set; they
        // must never silently replace the standard conditional headers.
        var policy = await ResolvePolicyAsync(
            CorsConfiguration.ProductionPolicy,
            isDevelopment: false,
            new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://app.example.com",
                ["Cors:AllowedHeaders:0"] = "X-Custom-Header"
            });

        Assert.Contains("X-Custom-Header", policy.Headers);
        Assert.Contains("If-Match", policy.Headers);
        Assert.Contains("Content-Type", policy.Headers);
    }

    [UnitTheory]
    [InlineData("https://console.example.com", true)]
    [InlineData("https://other.example.com", false)]
    [InlineData("https://console.example.com:8443", false)]
    [InlineData("null", false)]
    public async Task CredentialedPolicy_OnlyAllowsExplicitOrigins(string origin, bool allowed)
    {
        var policy = await ResolvePolicyAsync(CorsConfiguration.ProductionPolicy, false,
            new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://*.example.com",
                ["Cors:AllowedOrigins:1"] = "https://console.example.com",
                ["Cors:AllowCredentials"] = "true"
            });
        Assert.True(policy.SupportsCredentials);
        Assert.Equal(allowed, policy.IsOriginAllowed(origin));
    }

    [UnitTest]
    public async Task DevelopmentPolicy_WithCredentials_RejectsWildcardOrigins()
    {
        var policy = await ResolvePolicyAsync(CorsConfiguration.DevelopmentPolicy, true,
            new Dictionary<string, string?>
            {
                ["Cors:DevelopmentOrigins:0"] = "*",
                ["Cors:DevelopmentOrigins:1"] = "http://localhost:3000"
            });
        Assert.True(policy.SupportsCredentials);
        Assert.False(policy.AllowAnyOrigin);
        Assert.True(policy.IsOriginAllowed("http://localhost:3000"));
        Assert.False(policy.IsOriginAllowed("https://other.example.com"));
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
