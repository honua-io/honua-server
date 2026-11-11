// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class FolderOperationIntegrationTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.Mcp)]
    [Endpoint("POST /mcp tools/call")]
    public async Task FolderMcpTools_ApprovedLifecycle_PersistsCanonicalMutations()
    {
        var initial = await SnapshotAsync();
        var listed = await CallMcpToolAsync("honua_list_folders", []);
        listed.GetProperty("status").GetString().Should().Be("Completed");
        listed.GetProperty("details").GetProperty("etag").GetString().Should().Be(initial.Etag);
        await ApplyFolderToolAsync("honua_create_folder", new() { ["name"] = "Survey", ["If-Match"] = initial.Etag });
        var created = await SnapshotAsync();
        var folder = created.Graph.Folders.Should().ContainSingle().Subject;
        await ApplyFolderToolAsync("honua_move_service", new()
        {
            ["serviceName"] = "test",
            ["folderId"] = folder.Metadata.Id,
            ["If-Match"] = created.Etag
        });
        var moved = await SnapshotAsync();
        moved.Graph.Services.Where(service => service.Metadata.Name == "test").Should()
            .OnlyContain(service => service.FolderId == folder.Metadata.Id);
        var discovered = await CallMcpToolAsync("honua_admin_services_list", new() { ["folderId"] = folder.Metadata.Id });
        discovered.GetProperty("status").GetString().Should().Be("Completed");
        using var folderPage = JsonDocument.Parse(discovered.GetProperty("details").GetProperty("response").GetString()!);
        var services = folderPage.RootElement.GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
        services.Should().ContainSingle(service => service.GetProperty("serviceName").GetString() == "test");
        services.Should().OnlyContain(service => service.GetProperty("folderId").GetString() == folder.Metadata.Id);
        var rootServices = await CallMcpToolAsync("honua_admin_services_list", new() { ["folderId"] = "root" });
        rootServices.GetProperty("status").GetString().Should().Be("Completed");
        using var rootPage = JsonDocument.Parse(rootServices.GetProperty("details").GetProperty("response").GetString()!);
        rootPage.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
            .Should().NotContain(service => service.GetProperty("serviceName").GetString() == "test");
        await ApplyFolderToolAsync("honua_update_folder", new()
        {
            ["id"] = folder.Metadata.Id,
            ["name"] = "Survey2",
            ["If-Match"] = moved.Etag
        });
        var renamed = await SnapshotAsync();
        renamed.Graph.Folders.Single().PreviousNames.Should().Contain("Survey");
        await ApplyFolderToolAsync("honua_move_service", new()
        {
            ["serviceName"] = "test",
            ["folderId"] = null,
            ["If-Match"] = renamed.Etag
        });
        var returned = await SnapshotAsync();
        returned.Graph.Services.Where(service => service.Metadata.Name == "test").Should()
            .OnlyContain(service => service.FolderId == null);
        await ApplyFolderToolAsync("honua_delete_folder", new()
        {
            ["id"] = folder.Metadata.Id,
            ["If-Match"] = returned.Etag
        });
        (await SnapshotAsync()).Graph.Folders.Single().Deleted.Should().BeTrue();
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Mcp)]
    [Endpoint("POST /mcp tools/call")]
    public async Task FolderMcpTools_DeniedService_DoesNotAcceptMoveProposal()
    {
        var initial = await SnapshotAsync();
        await ApplyAsync("folder.create", new() { ["name"] = "Destination", ["If-Match"] = initial.Etag });
        var before = await SnapshotAsync();
        _authorization.DeniedService = "test";
        var result = await CallMcpToolAsync("honua_move_service", new()
        {
            ["serviceName"] = "test",
            ["folderId"] = before.Graph.Folders.Single().Metadata.Id,
            ["If-Match"] = before.Etag
        });
        result.GetProperty("status").GetString().Should().Be("Denied");
        result.TryGetProperty("proposalId", out var proposal).Should().BeFalse();
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
    }

    private async Task ApplyFolderToolAsync(string name, Dictionary<string, string?> arguments)
    {
        var before = await SnapshotAsync();
        var result = await CallMcpToolAsync(name, arguments);
        result.GetProperty("status").GetString().Should().Be("RequiresApproval", result.GetRawText());
        var id = result.GetProperty("proposalId").GetString()!;
        (await _proposals.GetAsync(id))!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        (await SnapshotAsync()).Etag.Should().Be(before.Etag, "MCP submission cannot execute an unapproved mutation");
        await ApproveAsync(id);
        (await _proposals.GetAsync(id))!.Status.Should().Be(OperationProposalStatus.Succeeded);
        (await SnapshotAsync()).Etag.Should().NotBe(before.Etag);
    }

    private async Task<JsonElement> CallMcpToolAsync(string name, Dictionary<string, string?> arguments)
    {
        using var response = await _proposer.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name, arguments }
        });
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(payload);
        var result = document.RootElement.GetProperty("result");
        (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True).Should().BeFalse(payload);
        return result.GetProperty("structuredContent").Clone();
    }
}
