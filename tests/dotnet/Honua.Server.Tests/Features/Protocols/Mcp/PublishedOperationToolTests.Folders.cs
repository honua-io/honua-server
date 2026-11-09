// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.WorkflowPackages.Domain;
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
        bridge.Request!.Parameters.Should().NotContainKey("approvedProposalId");
        bridge.Request.Parameters.Should().NotContainKey("approvedPlanHash");
        bridge.Request.Parameters["If-Match"].Should().Be("etag");
        executor.SubmitCount.Should().Be(0);
    }

    [UnitTheory]
    [InlineData("null", true)]
    [InlineData("\"survey\"", true)]
    [InlineData("42", false)]
    [InlineData(null, false)]
    public void Describe_ServiceMove_ValidatesRequiredRootOrFolderSelection(string? selection, bool expected)
    {
        var operation = AdminApiOperationCatalog.Descriptors.Single(descriptor => descriptor.OperationId == "service.move");
        var advertised = new PublishedOperationTool(operation, "catalog", NullLogger.Instance).Describe().InputSchema;
        var schema = JSchema.Parse(advertised.GetRawText());
        var arguments = JObject.Parse("""{"serviceName":"survey","If-Match":"etag"}""");
        if (selection is not null)
        {
            arguments["folderId"] = JToken.Parse(selection);
        }

        arguments.IsValid(schema).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(true, "null", false)]
    [InlineData(true, "\"survey\"", true)]
    [InlineData(true, "\"other\"", false)]
    [InlineData(true, "42", false)]
    [InlineData(false, "null", false)]
    [InlineData(false, "\"survey\"", true)]
    public void Describe_EnumSelection_ValidatesNullableAndNonnullableValues(bool allowsNull, string selection, bool expected)
    {
        var schema = SelectionSchema(new()
        {
            Type = WorkflowSchemaValueType.Text,
            AllowsNull = allowsNull,
            EnumValues = ["survey"]
        });
        var arguments = new JObject { ["selection"] = JToken.Parse(selection) };

        arguments.IsValid(schema).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Describe_ExplicitNullEnumMember_StillRequiresNullableType(bool allowsNull, bool expected)
    {
        var schema = SelectionSchema(new()
        {
            Type = WorkflowSchemaValueType.Text,
            AllowsNull = allowsNull,
            EnumValues = ["survey", null]
        });

        JObject.Parse("""{"selection":null}""").IsValid(schema).Should().Be(expected);
    }


    private static JSchema SelectionSchema(WorkflowSchemaDefinition selection)
    {
        var operation = DeterministicReadOnlyDescriptor() with
        {
            InputSchema = [Param("selection", true) with { Schema = selection }]
        };
        var advertised = new PublishedOperationTool(operation, "catalog", NullLogger.Instance).Describe().InputSchema;
        return JSchema.Parse(advertised.GetRawText());
    }

}
