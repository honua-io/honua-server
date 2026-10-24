// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class DatabricksStatementCancellationTests
{
    [Fact]
    public async Task ExecuteAsync_RunningUntilTimeout_CancelsTheStatement()
    {
        var handler = new RunningStatementHandler();
        var client = CreateClient(handler, commandTimeoutSeconds: 1);

        var act = () => client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(act);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/api/2.0/sql/statements/stmt-1/cancel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_RequestAbortedAfterSubmit_CancelsTheStatement()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RunningStatementHandler(cancellation);
        var client = CreateClient(handler, commandTimeoutSeconds: 30);

        var act = () => client.ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/api/2.0/sql/statements/stmt-1/cancel", StringComparison.Ordinal));
    }

    private static DatabricksStatementClient CreateClient(HttpMessageHandler handler, int commandTimeoutSeconds)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.cloud.databricks.com") };
        var options = Options.Create(new DatabricksOptions
        {
            Host = "https://example.cloud.databricks.com",
            WarehouseId = "wh123",
            Token = "secret-token",
            CommandTimeoutSeconds = commandTimeoutSeconds,
            PollIntervalMilliseconds = 20,
        });
        return new DatabricksStatementClient(httpClient, options);
    }

    private sealed class RunningStatementHandler : HttpMessageHandler
    {
        private readonly CancellationTokenSource? _abortAfterSubmit;

        public RunningStatementHandler(CancellationTokenSource? abortAfterSubmit = null)
        {
            _abortAfterSubmit = abortAfterSubmit;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_abortAfterSubmit is not null
                && request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath.Contains("/statements/stmt-1", StringComparison.Ordinal) == true)
            {
                _abortAfterSubmit.Cancel();
            }

            var content = new StringContent("""{"statement_id":"stmt-1","status":{"state":"RUNNING"}}""");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
