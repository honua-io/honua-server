// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin.OperationCatalog;

public sealed class AdminEndpointOperationInvokerTests
{
    private readonly AdminOpenApiOperationCatalog _catalog = new(FindAdminOpenApi());

    [UnitTest]
    public async Task InvokeAsync_CapturedTenantMissingFromCatalog_FailsClosed()
    {
        var endpointCalled = false;
        var definition = _catalog.GetRequired("admin.server.status");
        var tenantCatalog = Substitute.For<ITenantCatalog>();
        tenantCatalog.GetAsync("missing-tenant", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantRecord?>(null));
        var services = new ServiceCollection()
            .AddSingleton(tenantCatalog)
            .BuildServiceProvider();
        var invoker = CreateInvoker(
            services,
            definition,
            context =>
            {
                endpointCalled = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition),
            new OperationPolicyContext { TenantId = "missing-tenant" },
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Failed);
        result.Reason.Should().Be("Tenant access is unavailable.");
        result.Result!.Details["httpStatus"].Should()
            .Be(((int)HttpStatusCode.Forbidden).ToString(CultureInfo.InvariantCulture));
        endpointCalled.Should().BeFalse();
    }

    [UnitTest]
    public async Task InvokeAsync_RequestBodyExceedsConfiguredUploadLimit_Returns413BeforeRouting()
    {
        var definition = _catalog.GetRequired("admin.connection.create");
        var invoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition: null,
            handler: null,
            maxUploadBytes: 16);

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition, new string('x', 17)),
            new OperationPolicyContext(),
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Failed);
        result.Result!.Details["httpStatus"]
            .Should().Be(((int)HttpStatusCode.RequestEntityTooLarge).ToString(CultureInfo.InvariantCulture));
        result.Reason.Should().Contain("16-byte upload limit");
    }

    [UnitTest]
    public async Task InvokeAsync_LargeEndpointResponse_CapturesOnlyBoundedPrefix()
    {
        var definition = _catalog.GetRequired("admin.server.status");
        var response = new string('x', AdminEndpointOperationInvoker.MaxCapturedResponseBytes + 1024);
        var invoker = CreateInvoker(
            new ServiceCollection().BuildServiceProvider(),
            definition,
            context => context.Response.WriteAsync(response));

        var result = await invoker.InvokeAsync(
            definition,
            Request(definition),
            new OperationPolicyContext(),
            definition.Descriptor.OperationId,
            CancellationToken.None);

        result.Status.Should().Be(OperationHandleStatus.Completed);
        result.Result!.Details["response"].Should().Be("[REDACTED]",
            "a truncated non-JSON body cannot be proven credential-safe");
        result.Result.Details["responseTruncated"].Should().Be(bool.TrueString);
    }

    [UnitTest]
    public void Redact_NestedCredentialValues_RemovesSecretsAndPreservesReferences()
    {
        const string response =
            """{"data":{"apiKey":"hnua_secret","nested":[{"clientSecret":"oauth-secret","secretReference":"aws-sm://honua/control-plane","accessKey":"cloud-secret"}],"token":"bearer-secret","connectionString":"Host=db;Password=secret"}}""";

        var redacted = AdminOperationResponseRedactor.Redact(response);

        redacted.Should().NotContain("hnua_secret");
        redacted.Should().NotContain("oauth-secret");
        redacted.Should().NotContain("bearer-secret");
        redacted.Should().NotContain("cloud-secret");
        redacted.Should().NotContain("Host=db");
        redacted.Should().Contain("aws-sm://honua/control-plane");
        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        document.RootElement.GetProperty("data").GetProperty("apiKey").GetString().Should().Be("[REDACTED]");
        document.RootElement.GetProperty("data").GetProperty("nested")[0]
            .GetProperty("clientSecret").GetString().Should().Be("[REDACTED]");
    }

    private static AdminEndpointOperationInvoker CreateInvoker(
        IServiceProvider services,
        AdminOpenApiOperationDefinition? definition,
        RequestDelegate? handler,
        long maxUploadBytes = 1024 * 1024)
    {
        EndpointDataSource[] sources = definition is null || handler is null
            ? []
            : [CreateDataSource(definition, handler)];
        return new AdminEndpointOperationInvoker(
            sources,
            Substitute.For<IAuthorizationPolicyProvider>(),
            Substitute.For<IPolicyEvaluator>(),
            services,
            TimeProvider.System,
            Options.Create(new LimitsOptions { MaxUploadSizeBytes = maxUploadBytes }));
    }

    private static DefaultEndpointDataSource CreateDataSource(
        AdminOpenApiOperationDefinition definition,
        RequestDelegate handler)
    {
        var builder = new RouteEndpointBuilder(handler, RoutePatternFactory.Parse(definition.Path), order: 0);
        builder.Metadata.Add(new HttpMethodMetadata([definition.Method]));
        return new DefaultEndpointDataSource(builder.Build());
    }

    private static OperationRequest Request(AdminOpenApiOperationDefinition definition, string? body = null)
        => new()
        {
            OperationId = definition.Descriptor.OperationId,
            Parameters = body is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(StringComparer.Ordinal) { ["body"] = body },
        };

    private static string FindAdminOpenApi()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "admin-openapi.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "docs", "developer", "api-specs", "admin-api.json")),
        };

        return candidates.First(File.Exists);
    }
}
