// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Geoprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

internal sealed class StudioMcpDraftHarness
{
    private const long SeededGeneration = 2;

    private readonly IGeoprocessingJobService _jobService;
    private readonly HttpContext _httpContext;
    private readonly Guid _draftId;

    private StudioMcpDraftHarness(IGeoprocessingJobService jobService, HttpContext httpContext, Guid draftId)
    {
        _jobService = jobService;
        _httpContext = httpContext;
        _draftId = draftId;
    }

    public static async Task<StudioMcpDraftHarness> SeedMapDraftAsync(IGeoprocessingJobService jobService, string bodyJson)
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        var provider = services.BuildServiceProvider();
        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(requestServices =>
        {
            requestServices.AddSingleton(provider.GetRequiredService<IStudioPackageLifecycleService>());
            requestServices.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            McpTestFactory.AddAllowingStudioAuthorization(requestServices);
        });

        var created = await new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance).InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson("""{"packageKey":"parity-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);
        var draftId = created.StructuredContent!.Value.GetProperty("draftId").GetGuid();

        var seeded = await new UpdateStudioDraftTool(jobService, NullLogger<UpdateStudioDraftTool>.Instance).InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                "{\"draftId\":\"" + draftId + "\",\"generation\":1,\"packageKey\":\"parity-map\",\"schemaVersion\":\"1.0\",\"body\":"
                + bodyJson + "}"),
            CancellationToken.None);
        seeded.IsError.Should().BeFalse(seeded.Content[0].Text);
        return new StudioMcpDraftHarness(jobService, httpContext, draftId);
    }

    public async Task<JsonElement> CallAndReadBodyAsync(IMcpTool tool, string toolArgumentsJson)
    {
        var result = await tool.InvokeAsync(_httpContext, DraftArguments(toolArgumentsJson), CancellationToken.None);
        result.IsError.Should().BeFalse(result.Content[0].Text);
        return result.StructuredContent!.Value.GetProperty("envelope").GetProperty("body");
    }

    public Task<JsonElement> CallExpectingErrorAsync(IMcpTool tool, string toolArgumentsJson) =>
        CallExpectingErrorOnElementAsync(tool, DraftArguments(toolArgumentsJson));

    public Task<JsonElement> CallExpectingErrorFromArgumentTextAsync(IMcpTool tool, string argumentObjectJson)
    {
        var members = argumentObjectJson.Trim();
        if (members.Length < 2 || members[0] != '{')
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(argumentObjectJson));
        }

        var json = "{\"draftId\":\"" + _draftId.ToString("D") + "\",\"generation\":" + SeededGeneration + "," + members[1..];
        return CallExpectingErrorOnElementAsync(tool, McpTestFactory.ParseJson(json));
    }

    private async Task<JsonElement> CallExpectingErrorOnElementAsync(IMcpTool tool, JsonElement arguments)
    {
        try
        {
            await tool.InvokeAsync(_httpContext, arguments, CancellationToken.None);
        }
        catch (Exception exception) when (exception is GeoprocessingValidationException or GeoprocessingNotFoundException)
        {
            return McpToolHelpers.ErrorResult(exception).StructuredContent!.Value;
        }

        throw new InvalidOperationException($"{tool.Name} accepted the call.");
    }

    public async Task<JsonElement> ReadDraftAsync()
    {
        var result = await new GetStudioDraftTool(_jobService, NullLogger<GetStudioDraftTool>.Instance).InvokeAsync(
            _httpContext, McpTestFactory.ParseJson("{\"draftId\":\"" + _draftId + "\"}"), CancellationToken.None);
        result.IsError.Should().BeFalse(result.Content[0].Text);
        return result.StructuredContent!.Value.Clone();
    }

    private JsonElement DraftArguments(string toolArgumentsJson)
    {
        var arguments = JsonNode.Parse(toolArgumentsJson)!.AsObject();
        arguments["draftId"] = _draftId.ToString("D");
        arguments["generation"] = SeededGeneration;
        return McpTestFactory.ParseJson(arguments.ToJsonString());
    }
}
