// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Azure.Core;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class AzureBatchEndpointHardeningTests
{
    private const string ValidAccountUrl = "https://acct.eastus.batch.azure.com";

    [UnitTheory]
    [InlineData("https://acct.eastus.batch.azure.com")]
    [InlineData("https://acct.chinanorth.batch.chinacloudapi.cn/")]
    [InlineData("https://acct.usgovvirginia.batch.usgovcloudapi.net")]
    [InlineData("http://localhost:10000")]
    public void ValidateAccountUrl_AcceptsBatchHostsAndLoopback(string accountUrl)
    {
        AzureBatchDataPlaneClient.ValidateAccountUrl(accountUrl).Should().BeNull();
    }

    [UnitTheory]
    [InlineData("http://acct.eastus.batch.azure.com")]
    [InlineData("https://acct.eastus.batch.azure.com.attacker.example")]
    [InlineData("https://batch.azure.com")]
    [InlineData("https://user:pass@acct.eastus.batch.azure.com")]
    [InlineData("not a url")]
    public void ValidateAccountUrl_RejectsPlainHttpForeignHostsAndUserInfo(string accountUrl)
    {
        AzureBatchDataPlaneClient.ValidateAccountUrl(accountUrl).Should().NotBeNull();
    }

    [UnitTest]
    public async Task GetPoolStateAsync_WithForeignHost_NeverAcquiresTokenOrSends()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var credential = new CountingCredential();
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, credential);

        var act = () => client.GetPoolStateAsync("https://acct.attacker.example", "pool");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("Azure Batch account URL", StringComparison.Ordinal));
        credential.Calls.Should().Be(0);
        handler.Calls.Should().Be(0);
    }

    [UnitTest]
    public async Task GetPoolStateAsync_ErrorBody_IsRedactedAndTruncatedInException()
    {
        var body = "{\"message\":\"Authorization: Bearer eyJsecret-token\",\"sig\":\"abc123\"}" + new string('x', 2000);
        using var handler = new RecordingHandler(HttpStatusCode.Forbidden, body);
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, new CountingCredential());

        var act = () => client.GetPoolStateAsync(ValidAccountUrl, "pool");

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Which.Message.Should().NotContain("eyJsecret-token").And.NotContain("abc123");
        thrown.Which.Message.Length.Should().BeLessThan(700);
    }

    [UnitTest]
    public async Task StartAsync_WithPlainHttpAccountUrl_FailsWithoutCallingProvider()
    {
        var batchClient = Substitute.For<IAzureBatchClient>();
        var backend = new AzureBatchComputeBackend(batchClient, NullLogger<AzureBatchComputeBackend>.Instance);

        var result = await backend.StartAsync(CreateJob("http://acct.eastus.batch.azure.com"));

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.Message.Should().Contain("https");
        batchClient.ReceivedCalls().Should().BeEmpty();
    }

    [UnitTest]
    public async Task ObserveAsync_WithForeignAccountHost_FinalizesWithoutCallingProvider()
    {
        var batchClient = Substitute.For<IAzureBatchClient>();
        var backend = new AzureBatchComputeBackend(batchClient, NullLogger<AzureBatchComputeBackend>.Instance);

        var observation = await backend.ObserveAsync(CreateJob("https://acct.attacker.example"));

        observation.Status.Should().Be(ExecutionJobStatus.Failed);
        observation.Message.Should().Contain("Azure Batch account URL");
        batchClient.ReceivedCalls().Should().BeEmpty();
    }

    private static AzureBatchDataPlaneClient CreateClient(HttpClient httpClient, TokenCredential credential)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        return new AzureBatchDataPlaneClient(factory, NullLogger<AzureBatchDataPlaneClient>.Instance, credential);
    }

    private static ExecutionJobRecord CreateJob(string accountUrl)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = $"job-{Guid.NewGuid():N}",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                TargetKind = BatchComputeTargetKind.AzureBatch,
                Backend = AzureBatchComputeBackend.BackendIdentifier,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = "test-workload",
                WorkloadId = "wl-1",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["azure.batch.account_url"] = accountUrl,
                    ["azure.batch.pool_id"] = "default-pool",
                },
            },
        };
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class CountingCredential : TokenCredential
    {
        public int Calls { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            return new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
