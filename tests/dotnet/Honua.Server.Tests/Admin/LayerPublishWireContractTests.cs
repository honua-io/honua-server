// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Admin;

public sealed class LayerPublishWireContractTests
{
    [UnitTest]
    public async Task Accepted_EmitsDocumentedHandleAndLocation_WithoutACompletedLayer()
    {
        const string statusUrl = "/api/v1/admin/connections/roads/layers/jobs/publish";
        var result = TypedResults.Accepted(statusUrl,
            ApiResponse<LayerPublishAcceptedResponse>.CreateSuccess(new LayerPublishAcceptedResponse
            {
                JobId = "publish",
                StatusUrl = statusUrl,
                RowsTotal = 330000,
                Status = "Queued",
            }));
        var response = await ExecuteAsync(result);
        using var body = response.Body;
        using var spec = LoadSpec();
        Assert.Equal(202, response.Status);
        Assert.Equal(statusUrl, response.Location);
        var payload = body.RootElement.GetProperty("data");
        Assert.Equal(statusUrl, payload.GetProperty("statusUrl").GetString());
        Assert.Equal(330000, payload.GetProperty("rowsTotal").GetInt64());
        Assert.False(payload.TryGetProperty("layerId", out _));
        var schema = ResponseDataSchema(spec, "/connections/{id}/layers", "post", "202");
        AssertDocumentedProperties(schema, payload);
        Assert.Equal("Queued", Assert.Single(schema.GetProperty("properties").GetProperty("status")
            .GetProperty("enum").EnumerateArray()).GetString());
        Assert.True(spec.RootElement.GetProperty("paths").GetProperty("/connections/{id}/layers")
            .GetProperty("post").GetProperty("responses").GetProperty("202").GetProperty("headers")
            .TryGetProperty("Location", out _));
    }

    [UnitTheory]
    [InlineData("Processing", null, null)]
    [InlineData("Completed", 23, null)]
    [InlineData("Failed", null, "Layer publish failed.")]
    [InlineData("Cancelled", null, null)]
    public async Task Poll_EmitsDocumentedStateAndOptionalResult(string status, int? layerId, string? error)
    {
        var response = await ExecuteAsync(TypedResults.Ok(
            ApiResponse<LayerPublishJobStatusResponse>.CreateSuccess(new LayerPublishJobStatusResponse
            {
                JobId = "publish",
                Status = status,
                RowsMaterialized = layerId.HasValue ? 330000 : 1000,
                RowsTotal = 330000,
                LayerId = layerId,
                ErrorMessage = error,
            })));
        using var body = response.Body;
        using var spec = LoadSpec();
        Assert.Equal(200, response.Status);
        var payload = body.RootElement.GetProperty("data");
        var schema = ResponseDataSchema(spec, "/connections/{id}/layers/jobs/{jobId}", "get", "200");
        AssertDocumentedProperties(schema, payload);
        Assert.Equal(6, schema.GetProperty("properties").EnumerateObject().Count());
        Assert.Contains(status, schema.GetProperty("properties").GetProperty("status").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(status, payload.GetProperty("status").GetString());
        Assert.Equal(layerId.HasValue ? 330000 : 1000, payload.GetProperty("rowsMaterialized").GetInt64());
        Assert.Equal(330000, payload.GetProperty("rowsTotal").GetInt64());
        Assert.True(schema.GetProperty("properties").GetProperty("layerId").GetProperty("nullable").GetBoolean());
        Assert.True(schema.GetProperty("properties").GetProperty("errorMessage").GetProperty("nullable").GetBoolean());
        var actualLayerId = payload.GetProperty("layerId");
        Assert.Equal(layerId, actualLayerId.ValueKind == JsonValueKind.Null ? null : actualLayerId.GetInt32());
        Assert.Equal(error, payload.GetProperty("errorMessage").GetString());
    }

    [UnitTest]
    public async Task Refusal_EmitsDocumented503ProblemAndMachineReadableRemediation()
    {
        var response = await ExecuteAsync(context => LayerPublishRefusals.ToResult(context));
        using var body = response.Body;
        using var spec = LoadSpec();
        Assert.Equal(503, response.Status);
        var reference = spec.RootElement.GetProperty("paths").GetProperty("/connections/{id}/layers")
            .GetProperty("post").GetProperty("responses").GetProperty("503").GetProperty("content")
            .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString()!;
        var schema = spec.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(reference.Split('/')[^1]).GetProperty("allOf")[1];
        foreach (var name in schema.GetProperty("required").EnumerateArray())
        {
            Assert.True(body.RootElement.TryGetProperty(name.GetString()!, out _));
        }
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            if (property.Value.TryGetProperty("enum", out var values))
            {
                Assert.Equal(Assert.Single(values.EnumerateArray()).GetRawText(),
                    body.RootElement.GetProperty(property.Name).GetRawText());
            }
        }
        Assert.False(body.RootElement.TryGetProperty("jobId", out _));
    }

    private static JsonDocument LoadSpec()
        => JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("docs", "developer", "api-specs", "admin-api.json")));

    private static JsonElement ResponseDataSchema(JsonDocument spec, string path, string method, string status)
    {
        var schemas = spec.RootElement.GetProperty("components").GetProperty("schemas");
        var reference = spec.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString()!;
        var dataReference = schemas.GetProperty(reference.Split('/')[^1]).GetProperty("properties")
            .GetProperty("data").GetProperty("$ref").GetString()!;
        return schemas.GetProperty(dataReference.Split('/')[^1]);
    }

    private static void AssertDocumentedProperties(JsonElement schema, JsonElement payload)
    {
        foreach (var property in payload.EnumerateObject())
        {
            Assert.True(schema.GetProperty("properties").TryGetProperty(property.Name, out _), property.Name);
        }
        foreach (var required in schema.GetProperty("required").EnumerateArray())
        {
            Assert.True(payload.TryGetProperty(required.GetString()!, out _), required.GetString());
        }
    }

    private static Task<(int Status, string? Location, JsonDocument Body)> ExecuteAsync(IResult result)
        => ExecuteAsync(_ => result);

    private static async Task<(int Status, string? Location, JsonDocument Body)> ExecuteAsync(Func<HttpContext, IResult> result)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddHonuaJsonContexts()
            .BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = body },
        };
        await result(context).ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.Headers.Location.ToString(), JsonDocument.Parse(body.ToArray()));
    }
}
