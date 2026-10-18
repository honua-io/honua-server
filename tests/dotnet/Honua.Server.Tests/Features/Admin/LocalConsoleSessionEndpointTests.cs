// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

public sealed class LocalConsoleSessionEndpointTests
{
    private const string Key = "local-bridge-secret-longer-than-thirty-two-bytes";

    [Theory]
    [InlineData(false, "Development", Key, false, 404)]
    [InlineData(true, "Production", Key, false, 404)]
    [InlineData(true, "Development", "wrong-key", false, 401)]
    [InlineData(true, "Development", Key, true, 401)]
    public async Task ExchangeFailsClosed(bool enabled, string environment, string supplied, bool agent, int status)
    {
        var (context, configuration, tokens) = Setup(enabled, supplied);
        if (agent) context.Request.Headers["X-API-Key"] = "agent-key";
        var result = await LocalConsoleSessionEndpoint.HandleAsync(context, configuration, new Host(environment), tokens);
        Assert.Equal(status, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task ExchangeIssuesValidatedFixedTenantHumanWithBoundedLifetime()
    {
        var (context, configuration, tokens) = Setup(true, Key);
        var result = Assert.IsType<Ok<AdminOperatorBearerResponse>>(
            await LocalConsoleSessionEndpoint.HandleAsync(context, configuration, new Host("Development"), tokens));
        var claims = await tokens.TryValidateAsync(result.Value!.AccessToken);
        Assert.Contains(claims!, claim => claim.Type == "tenant_id" && claim.Value == "public");
        Assert.Contains(claims!, claim => claim.Type == ClaimTypes.NameIdentifier && claim.Value == "local-console:operator-1");
        Assert.Contains(claims!, claim => claim.Type == "roles" && claim.Value == "admin");
        Assert.InRange(result.Value.ExpiresIn, 1, 600);
    }

    [Fact]
    public async Task ClientCannotSupplyTenant()
    {
        var (context, configuration, tokens) = Setup(true, Key);
        var body = Encoding.UTF8.GetBytes("{\"subject\":\"operator-1\",\"tenantId\":\"other\"}");
        context.Request.Body = new MemoryStream(body); context.Request.ContentLength = body.Length;
        var result = await LocalConsoleSessionEndpoint.HandleAsync(context, configuration, new Host("Development"), tokens);
        Assert.Equal(400, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private static (DefaultHttpContext, IConfiguration, OperatorBearerTokenService) Setup(bool enabled, string supplied)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentWorkspace:LocalHumanBridge:Enabled"] = enabled.ToString(),
            ["AgentWorkspace:LocalHumanBridge:Key"] = Key,
            ["AgentWorkspace:LocalHumanBridge:TenantId"] = "public"
        }).Build();
        var context = new DefaultHttpContext();
        var body = Encoding.UTF8.GetBytes("{\"subject\":\"operator-1\"}");
        context.Request.Body = new MemoryStream(body); context.Request.ContentLength = body.Length;
        context.Request.Headers["X-Honua-Local-Bridge"] = supplied;
        return (context, configuration, new OperatorBearerTokenService(Options.Create(new OperatorBearerOptions
        { Enabled = true, SigningKey = "signing-key-independent-from-bridge-at-least-32bytes" })));
    }

    private sealed class Host(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
