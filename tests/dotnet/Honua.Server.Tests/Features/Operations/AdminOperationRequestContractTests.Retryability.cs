// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class AdminOperationRequestContractTests
{
    [UnitTheory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "true", 5)]
    [InlineData(HttpStatusCode.TooManyRequests, "true", 15)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "false", null)]
    public async Task ConnectImportExecutor_RetryableHeaders_PropagateToHandleAndCallerResponse(
        HttpStatusCode statusCode,
        string retryable,
        int? retryAfterSeconds)
    {
        using var handler = new RetryabilityStubHandler(statusCode, retryable, retryAfterSeconds);
        using var client = new HttpClient(handler);
        var callerContext = CreateLoopbackCallerContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(callerContext);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminConnectImportOperationExecutor.HttpClientName).Returns(client);
        var executor = new AdminConnectImportOperationExecutor(
            AdminConnectImportOperationCatalog.Definitions.Single(d => d.OperationId == "admin.connections.list"),
            factory,
            accessor,
            new InMemoryAdminApiKeyStore(TimeProvider.System),
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));

        var handle = await executor.SubmitAsync(
            new OperationRequest { OperationId = executor.OperationId },
            new OperationPolicyContext());

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Result!.Details["statusCode"].Should().Be(((int)statusCode).ToString(CultureInfo.InvariantCulture));
        handle.Result.Details["retryable"].Should().Be(retryable);
        callerContext.Response.Headers["Honua-Retryable"].ToString().Should().Be(retryable);
        if (retryAfterSeconds is { } seconds)
        {
            var expectedSeconds = seconds.ToString(CultureInfo.InvariantCulture);
            handle.Result.Details["retryAfterSeconds"].Should().Be(expectedSeconds);
            callerContext.Response.Headers.RetryAfter.ToString().Should().Be(expectedSeconds);
        }
        else
        {
            handle.Result.Details.Should().NotContainKey("retryAfterSeconds");
        }
    }

    [UnitTheory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "true", 5)]
    [InlineData(HttpStatusCode.TooManyRequests, "true", 20)]
    public async Task OperateExecutor_RetryableHeaders_PropagateToHandleAndCallerResponse(
        HttpStatusCode statusCode,
        string retryable,
        int retryAfterSeconds)
    {
        using var handler = new RetryabilityStubHandler(statusCode, retryable, retryAfterSeconds);
        using var client = new HttpClient(handler);
        var callerContext = CreateLoopbackCallerContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(callerContext);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminOperateOperationExecutor.HttpClientName).Returns(client);
        var definition = AdminOperateOperationCatalog.Definitions.Single(d => d.OperationId == "admin.cache.status");
        var descriptor = AdminOperateOperationCatalog.Descriptors.Single(d => d.OperationId == "admin.cache.status");
        var executor = new AdminOperateOperationExecutor(
            definition,
            descriptor,
            factory,
            accessor,
            new InMemoryAdminApiKeyStore(TimeProvider.System),
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));

        var handle = await executor.SubmitAsync(
            new OperationRequest { OperationId = executor.OperationId },
            new OperationPolicyContext());

        var expectedSeconds = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Result!.Details["statusCode"].Should().Be(((int)statusCode).ToString(CultureInfo.InvariantCulture));
        handle.Result.Details["retryable"].Should().Be(retryable);
        handle.Result.Details["retryAfterSeconds"].Should().Be(expectedSeconds);
        callerContext.Response.Headers["Honua-Retryable"].ToString().Should().Be(retryable);
        callerContext.Response.Headers.RetryAfter.ToString().Should().Be(expectedSeconds);
    }

    [UnitTest]
    public async Task ApiExecutor_429WithoutExplicitHeader_InfersRetryableAndPreservesRetryAfter()
    {
        using var handler = new RetryabilityStubHandler(HttpStatusCode.TooManyRequests, retryable: null, retryAfterSeconds: 30);
        using var client = new HttpClient(handler);
        var callerContext = CreateLoopbackCallerContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(callerContext);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminApiOperationExecutor.HttpClientName).Returns(client);
        var definition = AdminApiOperationCatalog.Definitions.Single(d => d.OperationId == "admin.services.list");
        var executor = new AdminApiOperationExecutor(
            definition,
            factory,
            accessor,
            new InMemoryAdminApiKeyStore(TimeProvider.System),
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System));

        var handle = await executor.SubmitAsync(
            new OperationRequest { OperationId = executor.OperationId },
            new OperationPolicyContext());

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Result!.Details["statusCode"].Should().Be("429");
        handle.Result.Details["retryable"].Should().Be("true");
        handle.Result.Details["retryAfterSeconds"].Should().Be("30");
        callerContext.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        callerContext.Response.Headers.RetryAfter.ToString().Should().Be("30");
    }

    private static DefaultHttpContext CreateLoopbackCallerContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("honua.example", 443);
        context.Connection.LocalPort = 8080;
        return context;
    }

    private sealed class RetryabilityStubHandler(
        HttpStatusCode statusCode,
        string? retryable,
        int? retryAfterSeconds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("""{"title":"Temporarily unavailable"}""")
            };
            if (retryable is not null)
            {
                response.Headers.TryAddWithoutValidation("Honua-Retryable", retryable);
            }

            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return Task.FromResult(response);
        }
    }
}
