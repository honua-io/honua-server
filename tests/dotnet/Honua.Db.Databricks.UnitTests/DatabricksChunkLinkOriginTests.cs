// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public class DatabricksChunkLinkOriginTests
{
    private const string Workspace = "https://example.cloud.databricks.com";

    [Fact]
    public async Task AbsoluteChunkLinkOnTheWorkspace_IsFollowedWithTheToken()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(FirstChunkLinkingTo($"{Workspace}/api/2.0/sql/statements/abc/result/chunks/1")).EnqueueJson(LastChunk);
        using var httpClient = WorkspaceHttpClient(handler);

        var result = await CreateClient(httpClient).ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal($"{Workspace}/api/2.0/sql/statements/abc/result/chunks/1", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Requests[1].Headers.Authorization?.Scheme);
    }

    [Theory]
    [InlineData("https://collector.example.net/api/2.0/sql/statements/abc/result/chunks/1")]
    [InlineData("http://example.cloud.databricks.com/api/2.0/sql/statements/abc/result/chunks/1")]
    [InlineData("//collector.example.net/chunks/1")]
    public async Task ChunkLinkOutsideTheWorkspace_FailsWithoutSendingTheToken(string link)
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(FirstChunkLinkingTo(link));
        using var httpClient = WorkspaceHttpClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(httpClient).ExecuteAsync(DatabricksSqlStatement.WithoutParameters("SELECT 1"), CancellationToken.None));

        Assert.Contains("outside the configured workspace host", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    private const string LastChunk = """{ "result": { "data_array": [ ["2"] ] } }""";

    private static string FirstChunkLinkingTo(string link) => $$"""
        {
          "statement_id": "abc",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [ { "name": "c", "position": 0 } ] } },
          "result": { "data_array": [ ["1"] ], "next_chunk_internal_link": "{{link}}" }
        }
        """;

    private static HttpClient WorkspaceHttpClient(StubHttpMessageHandler handler)
        => new(handler, disposeHandler: false) { BaseAddress = new Uri(Workspace) };

    private static DatabricksStatementClient CreateClient(HttpClient httpClient)
    {
        var options = Options.Create(new DatabricksOptions
        {
            Host = Workspace,
            WarehouseId = "wh123",
            Token = "secret-token",
            CommandTimeoutSeconds = 30,
            PollIntervalMilliseconds = 1,
        });
        return new DatabricksStatementClient(httpClient, options);
    }
}
