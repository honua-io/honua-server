// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Databricks.Features.FeatureStore.Services;
using Honua.Databricks.Features.Infrastructure;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Honua.Databricks.Tests;

/// <summary>
/// Verifies the Statement Execution submit/poll loop transparently retries transient
/// workspace failures when the HTTP client is fronted by the shared resilience policy
/// (mirroring the production DI registration), without retrying non-transient errors.
/// </summary>
public class DatabricksStatementResilienceTests
{
    private const string SucceededJson = """
    {
      "statement_id": "abc",
      "status": { "state": "SUCCEEDED" },
      "manifest": { "schema": { "columns": [ { "name": "c", "position": 0 } ] } },
      "result": { "data_array": [ ["42"] ] }
    }
    """;

    private static DatabricksStatementClient CreateClient(StubHttpMessageHandler handler, int maxRetries)
    {
        // A fresh service type per test isolates the cached circuit-breaker state so tests
        // do not interfere with one another; tiny delays keep the backoff fast.
        var options = new ResiliencePolicyOptions
        {
            MaxRetryAttempts = maxRetries,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            BackoffExponent = 2.0,
            JitterPercentage = 0,
        };
        var policy = HttpResiliencePolicies.GetHttpPolicy($"databricks-test-{Guid.NewGuid():N}", options);
        var policyHandler = new PolicyHttpMessageHandler(policy) { InnerHandler = handler };

        var httpClient = new HttpClient(policyHandler) { BaseAddress = new Uri("https://example.cloud.databricks.com") };
        var databricksOptions = Options.Create(new DatabricksOptions
        {
            Host = "https://example.cloud.databricks.com",
            WarehouseId = "wh123",
            Token = "secret-token",
            CommandTimeoutSeconds = 30,
            PollIntervalMilliseconds = 1,
        });
        return new DatabricksStatementClient(httpClient, databricksOptions);
    }

    [Fact]
    public async Task ExecuteAsync_TransientServerErrorThenSuccess_RetriesAndCompletes()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("{}", HttpStatusCode.ServiceUnavailable)
            .EnqueueJson(SucceededJson);
        var client = CreateClient(handler, maxRetries: 3);

        var result = await client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        Assert.Single(result.Rows);
        // First submit got 503; the policy retried, and the second submit succeeded.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExecuteAsync_TooManyRequestsThenSuccess_RetriesAndCompletes()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("{}", HttpStatusCode.TooManyRequests)
            .EnqueueJson(SucceededJson);
        var client = CreateClient(handler, maxRetries: 3);

        var result = await client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        Assert.Single(result.Rows);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientError_DoesNotRetry()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("{}", HttpStatusCode.BadRequest);
        var client = CreateClient(handler, maxRetries: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None));

        // 400 is not transient: the single submit is surfaced without any retry.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_PersistentTransientError_ExhaustsRetriesThenThrows()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson("{}", HttpStatusCode.ServiceUnavailable)
            .EnqueueJson("{}", HttpStatusCode.ServiceUnavailable)
            .EnqueueJson("{}", HttpStatusCode.ServiceUnavailable);
        var client = CreateClient(handler, maxRetries: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None));

        // 1 initial attempt + 2 retries = 3 requests, all 503.
        Assert.Equal(3, handler.Requests.Count);
    }
}
