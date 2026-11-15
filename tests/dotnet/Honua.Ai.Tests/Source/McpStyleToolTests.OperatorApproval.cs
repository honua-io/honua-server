// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpStyleToolTests
{
    [Theory]
    [InlineData(PolicyDecisionKind.Allow)]
    [InlineData(PolicyDecisionKind.Deny)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_OperatorApproval_RoutesThroughCanonicalPolicy(PolicyDecisionKind policy)
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var sync = Substitute.For<IMetadataV2StyleGraphSync>();
        var bridge = Substitute.For<IOperationApprovalBridge>();
        bridge.CreateProposalAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<PolicyDecision>(), Arg.Any<CancellationToken>())
            .Returns(new OperationApprovalBridgeResult { IsDurable = true, ProposalId = "operator-style-proposal", AuditId = "operator-style-audit" });

        var response = await DispatchAsync(ApplyStylePresetTool.ToolName, StyleApprovalArguments,
            catalog: catalog, graphSync: sync, approvalRequired: true, policyDecision: policy, approvalBridge: bridge);

        var result = response!.Result!.Value.GetProperty("structuredContent");
        if (policy == PolicyDecisionKind.Allow)
        {
            result.GetProperty("proposalId").GetString().Should().Be("operator-style-proposal");
            result.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        }
        else
        {
            result.GetProperty("code").GetString().Should().Be("permission_denied");
            await bridge.DidNotReceiveWithAnyArgs().CreateProposalAsync(default!, default!, default!, default!, default);
        }
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await sync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_OperatorApproval_DryRunValidatesWithoutActuation()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var sync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            StyleApprovalArguments.TrimEnd('}') + ", \"dryRun\": true }",
            catalog: catalog, graphSync: sync, approvalRequired: true);

        response!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse();
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await sync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    private const string StyleApprovalArguments =
        "{ \"serviceId\": \"svc-parcels\", \"serviceLayerIndex\": 0, \"styleId\": \"style_flood_depth\" }";
}
