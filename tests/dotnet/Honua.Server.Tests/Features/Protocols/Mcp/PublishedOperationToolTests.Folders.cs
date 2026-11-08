// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class PublishedOperationToolTests
{
    [UnitTheory]
    [InlineData("folder.list", "honua_list_folders")]
    [InlineData("folder.create", "honua_create_folder")]
    [InlineData("folder.update", "honua_update_folder")]
    [InlineData("folder.delete", "honua_delete_folder")]
    [InlineData("service.move", "honua_move_service")]
    public void Describe_FolderOperation_UsesPublicToolNameAndCanonicalSchema(string operationId, string name)
    {
        var operation = AdminApiOperationCatalog.Descriptors.Single(descriptor => descriptor.OperationId == operationId);
        var descriptor = new PublishedOperationTool(operation, "catalog", NullLogger.Instance).Describe();
        descriptor.Name.Should().Be(name);
        descriptor.InputSchema.GetProperty("properties").TryGetProperty("namespace", out _).Should().BeTrue();
        descriptor.Annotations!.ReadOnlyHint.Should().Be(operationId == "folder.list");
        if (operationId != "folder.list")
        {
            descriptor.InputSchema.GetProperty("required").EnumerateArray()
                .Select(property => property.GetString()).Should().Contain("If-Match");
            descriptor.Description.Should().Contain("proposal only");
        }
    }

    [UnitTheory]
    [InlineData("folder.create")]
    [InlineData("folder.update")]
    [InlineData("folder.delete")]
    [InlineData("service.move")]
    public async Task Invoke_FolderMutation_OnlySubmitsCanonicalProposal(string operationId)
    {
        var operation = AdminApiOperationCatalog.Descriptors.Single(descriptor => descriptor.OperationId == operationId);
        var executor = new RecordingExecutor(operationId);
        var bridge = new CapturingProposalBridge();
        var dispatcher = new OperationDispatcher(
            new OperationCatalog([new AdminApiOperationDescriptorProvider()], TimeProvider.System), [executor],
            new AllowAllPolicyDecisionPoint(), TimeProvider.System, bridge);
        var tool = new PublishedOperationTool(operation, "catalog", NullLogger.Instance);
        var result = await tool.InvokeAsync(Context(dispatcher, authorization: ProposalAuthorization(), roles: ["admin"]),
            Args("""{"name":"Survey","id":"folder","serviceName":"test","folderId":null,"If-Match":"etag","approvedProposalId":"forged","approvedPlanHash":"forged"}"""), default);
        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("RequiresApproval");
        bridge.Calls.Should().Be(1);
        bridge.Context!.ApprovedProposalId.Should().BeNull();
        bridge.Context.ApprovedPlanHash.Should().BeNull();
        executor.SubmitCount.Should().Be(0);
    }
}
