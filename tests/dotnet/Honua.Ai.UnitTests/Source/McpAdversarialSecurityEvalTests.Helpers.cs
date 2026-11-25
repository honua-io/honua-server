// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpAdversarialSecurityEvalTests
{
    private static void AssertNoInternalLeak(string text)
    {
        text.Should().NotContain("SELECT ").And.NotContain("tenant_secrets")
            .And.NotContain("sk-live-999").And.NotContain("/srv/honua")
            .And.NotContain("Npgsql").And.NotContain("StackTrace");
    }

    private static string BuildToolCallJson(string id, string toolName, object arguments) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = toolName, arguments } });

    private static McpJsonRpcRequest BuildRpcRequest(string id, string method, object? parameters) => new()
    {
        JsonRpc = "2.0",
        Id = McpTestFactory.ParseJson(JsonSerializer.Serialize(id)),
        Method = method,
        Params = parameters is null ? null : McpTestFactory.ParseJson(JsonSerializer.Serialize(parameters))
    };

    private static DefaultHttpContext CreatePostContext(PostRequestSpec spec)
    {
        var tools = spec.Tool is null ? Array.Empty<IMcpTool>() : [spec.Tool];
        var surface = new McpDataAccessSurface(tools, [], NullLogger<McpDataAccessSurface>.Instance);
        var serviceCollection = new ServiceCollection()
            .AddSingleton(surface)
            .AddSingleton<ILogger<McpDataAccessSurface>>(NullLogger<McpDataAccessSurface>.Instance)
            .AddSingleton(Options.Create(spec.Options ?? new McpOptions()));
        if (spec.Sessions is not null)
        {
            serviceCollection.AddSingleton(spec.Sessions);
        }
        else
        {
            serviceCollection.AddSingleton<McpSessionManager>();
        }

        var services = serviceCollection.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        if (spec.Authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("api_key_id", Guid.NewGuid().ToString("D")), new Claim(ClaimTypes.Name, "eval-agent")],
                "ApiKey"));
        }

        if (spec.AcceptSse)
        {
            ConfigureSseJsonPost(context, spec.Body);
        }
        else
        {
            ConfigureJsonPost(context, spec.Body);
        }

        return context;
    }

    private static DefaultHttpContext CreateBearerPostContext(BearerRequestSpec spec)
    {
        var surface = new McpDataAccessSurface([spec.Tool], [], NullLogger<McpDataAccessSurface>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(surface)
            .AddSingleton(spec.Sessions)
            .AddSingleton(CreateTenantContext(spec.TenantId))
            .AddSingleton<ILogger<McpDataAccessSurface>>(NullLogger<McpDataAccessSurface>.Instance)
            .AddSingleton(Options.Create(new McpOptions()))
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "shared-agent-sub"), new Claim("iss", "https://idp.example")],
                "Bearer"))
        };
        context.Request.Headers.Authorization = "Bearer test-token";
        if (!string.IsNullOrEmpty(spec.SessionId))
        {
            context.Request.Headers[McpSessionManager.SessionHeaderName] = spec.SessionId;
        }

        ConfigureJsonPost(context, spec.Body);
        return context;
    }

    private static ITenantContext CreateTenantContext(string? tenantId)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        tenant.Source.Returns(tenantId is null ? TenantContextSource.Anonymous : TenantContextSource.Claim);
        return tenant;
    }

    private static void ConfigureJsonPost(DefaultHttpContext context, string body)
    {
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = McpEndpointExtensions.RoutePath;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
    }

    private static void ConfigureSseJsonPost(DefaultHttpContext context, string body)
    {
        ConfigureJsonPost(context, body);
        context.Request.Headers.Accept = "text/event-stream";
    }

    private static JsonDocument ReadJsonResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body);
    }

    private static string ReadRawResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class PostRequestSpec
    {
        public required string Body { get; init; }
        public IMcpTool? Tool { get; init; }
        public McpOptions? Options { get; init; }
        public McpSessionManager? Sessions { get; init; }
        public bool Authenticated { get; init; }
        public bool AcceptSse { get; init; }
    }

    private sealed class BearerRequestSpec
    {
        public required string Body { get; init; }
        public string? TenantId { get; init; }
        public required IMcpTool Tool { get; init; }
        public required McpSessionManager Sessions { get; init; }
        public string? SessionId { get; init; }
    }

    private sealed class EchoClosedSchemaTool : IMcpTool
    {
        private static readonly JsonElement Schema = McpTestFactory.ParseJson(
            """{"type":"object","additionalProperties":false,"properties":{"filter":{"type":"string"}}}""");

        public int Invocations { get; private set; }
        public string Name => "honua_eval_echo";
        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

        public McpToolDescriptor Describe() => new()
        {
            Name = Name,
            Title = "Eval echo",
            Description = "Closed-schema echo tool.",
            InputSchema = Schema
        };

        public Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
        {
            Invocations++;
            var echo = arguments is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("filter", out var filter)
                ? filter.GetString() ?? string.Empty
                : string.Empty;
            return Task.FromResult(McpToolHelpers.SuccessJsonElement(McpTestFactory.ParseJson(JsonSerializer.Serialize(new { echo }))));
        }
    }

    private sealed class ThrowingTool(Exception exception) : IMcpTool
    {
        private static readonly JsonElement Schema = McpTestFactory.ParseJson(
            """{"type":"object","additionalProperties":false,"properties":{}}""");

        public string Name => "honua_eval_throw";
        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

        public McpToolDescriptor Describe() => new()
        {
            Name = Name,
            Title = "Eval throw",
            Description = "Throws a synthetic error.",
            InputSchema = Schema
        };

        public Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
            => Task.FromException<McpToolsCallResult>(exception);
    }

    private sealed class InjectedMetadataResource(string payload) : IMcpResource
    {
        public string Family => "eval";

        public IReadOnlyList<McpResourceDescriptor> Describe() =>
        [
            new McpResourceDescriptor { Uri = "honua://eval/injected", Name = "eval-resource", Description = payload, MimeType = "application/json" }
        ];

        public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];
        public bool CanHandle(string uri) => string.Equals(uri, "honua://eval/injected", StringComparison.Ordinal);

        public Task<McpResourcesReadResult> ReadAsync(HttpContext httpContext, string uri, CancellationToken cancellationToken) =>
            Task.FromResult(new McpResourcesReadResult
            {
                Contents = [new McpResourceContent { Uri = uri, MimeType = "application/json", Text = JsonSerializer.Serialize(new { note = payload }) }]
            });
    }
}
