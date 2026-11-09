// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpConsoleEntitiesTests
{
    [UnitTheory]
    [InlineData("honua_execute_plan", "{\"jobId\":\"job/a ?#\"}", "/operate/jobs/job%2Fa%20%3F%23")]
    [InlineData("honua_list_jobs", "{\"jobs\":[{\"jobId\":\"j1\"}]}", "/operate/jobs/j1")]
    [InlineData("honua_admin_layer_publish", "{\"status\":\"RequiresApproval\",\"proposalId\":\"p?x&y\"}", "/inbox?proposalId=p%3Fx%26y")]
    public void Enrich_KnownIdentities_EncodesConsoleRoute(string tool, string json, string href)
    {
        var result = Result(json);
        McpConsoleEntities.Enrich(tool, result);
        result.Meta!.Value.GetProperty("honua/consoleEntities")[0]
            .GetProperty("href").GetString().Should().Be(href);
    }

    [UnitTheory]
    [InlineData("admin.services.list", "{\"items\":[{\"serviceName\":\"wire/a\",\"id\":\"opaque-id\",\"title\":\"Display\"}]}", "/operate/services/wire%2Fa/settings")]
    [InlineData("folder.list", "[{\"metadata\":{\"id\":\"folder/a\"},\"name\":\"Display\"}]", "/operate/folders/folder%2Fa")]
    [InlineData("folder.create", "{\"metadata\":{\"id\":\"new-folder\"}}", "/operate/folders/new-folder")]
    public void Enrich_CompletedCatalog_UsesCanonicalIdentity(string operation, string data, string href)
    {
        var result = OperationResult(operation, data);
        McpConsoleEntities.Enrich(PublishedOperationTool.ProjectName(operation), result);
        result.Meta!.Value.GetProperty("honua/consoleEntities")[0]
            .GetProperty("href").GetString().Should().Be(href);
    }

    [UnitTheory]
    [InlineData("honua_query_features", "{\"features\":[{\"properties\":{\"jobId\":\"spoof\",\"proposalId\":\"spoof\"}}]}")]
    [InlineData("honua_admin_layer_publish", "{\"status\":\"Denied\",\"proposalId\":\"private\",\"jobId\":\"private\"}")]
    [InlineData("honua_admin_layer_publish", "{\"status\":\"Failed\",\"resourceIds\":{\"serviceName\":\"private\"}}")]
    [InlineData("honua_list_layers", "{\"layers\":[{\"layerId\":\"not-numeric\"}]}")]
    [InlineData("honua_execute_plan", "{\"jobId\":17}")]
    public void Enrich_UntrustedOrUnusableIdentity_OmitsMetadata(string tool, string json)
    {
        var result = Result(json);
        McpConsoleEntities.Enrich(tool, result);
        result.Meta.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"data\":[{\"metadata\":null}]}")]
    public void Enrich_MalformedOptionalCatalog_PreservesResult(string response)
    {
        var result = OperationResult("folder.list", "[]");
        result.StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["status"] = "Completed",
            ["operationId"] = "folder.list",
            ["details"] = new Dictionary<string, string> { ["response"] = response }
        });
        var payload = result.StructuredContent;
        McpConsoleEntities.Enrich("honua_op_folder_list", result);
        result.Meta.Should().BeNull();
        result.StructuredContent.Should().Be(payload);
    }

    [UnitTest]
    public void Enrich_ErrorResult_DoesNotExposeIdentities()
    {
        var result = Result("{\"jobId\":\"private\"}");
        result.IsError = true;
        McpConsoleEntities.Enrich("honua_execute_plan", result);
        result.Meta.Should().BeNull();
    }

    [UnitTest]
    public void Enrich_LargeCatalog_BoundsMetadataAndPreservesExistingMetadata()
    {
        var jobs = string.Join(',', Enumerable.Range(0, 1000).Select(i => $"{{\"jobId\":\"j{i}\"}}"));
        var result = Result($"{{\"jobs\":[{jobs}]}}");
        result.Meta = Parse("{\"existing/key\":\"retained\"}");
        McpConsoleEntities.Enrich("honua_list_jobs", result);
        var metadata = result.Meta!.Value;
        metadata.GetProperty("existing/key").GetString().Should().Be("retained");
        metadata.GetProperty("honua/consoleEntities").GetArrayLength().Should().Be(100);
        metadata.GetRawText().Length.Should().BeLessThan(10_000);
    }

    private static McpToolsCallResult OperationResult(string operation, string data)
    {
        var response = JsonSerializer.Serialize($"{{\"data\":{data}}}");
        return Result($"{{\"status\":\"Completed\",\"operationId\":\"{operation}\",\"details\":{{\"response\":{response}}}}}");
    }

    private static McpToolsCallResult Result(string json) => new() { StructuredContent = Parse(json) };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
