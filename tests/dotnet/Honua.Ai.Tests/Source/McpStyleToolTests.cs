// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.Options;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Services;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the styling MCP tools (<c>honua_get_style</c> /
/// <c>honua_apply_style_preset</c>). Both are thin adapters over the canonical
/// styleId-keyed <see cref="IStyleCatalog"/> and the Metadata v2 style graph
/// (ADR-0048), so these tests substitute those seams and assert the adapters
/// read/bind styles correctly, that an unknown preset is rejected naming the
/// valid presets, that applying is gated on the authoring grant, and that a
/// subsequent <c>honua_render_map</c> resolves the applied style.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpStyleToolTests
{
    private const string ServiceId = "svc-parcels";
    private const string ServiceName = "Parcels";
    private const string ResourceId = "res-parcels";
    private const int LayerIndex = 0;
    private const int StorageLayerId = 42;
    private const string PresetStyleId = "style_flood_depth";

    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    private static StyleCatalogRecord Preset(string styleId = PresetStyleId, int version = 3) => new()
    {
        StyleId = styleId,
        Title = "Flood depth",
        Description = "Graduated flood depth ramp.",
        MapLibreStyleJson = "{\"version\":8,\"layers\":[]}",
        StyleVersion = version
    };

    // ---------------------------------------------------------------
    // honua_get_style
    // ---------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_style")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GetStyle_ByStyleId_ReturnsStyleRefWithInlinedStylesheet()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());

        var response = await DispatchAsync(
            GetStyleTool.ToolName,
            $$"""{ "styleId": "{{PresetStyleId}}", "encoding": "mapbox-style", "includeStylesheet": true }""",
            catalog: catalog);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        structured.GetProperty("styleId").GetString().Should().Be(PresetStyleId);
        structured.GetProperty("styleVersion").GetInt32().Should().Be(3);

        var encodings = structured.GetProperty("encodings").EnumerateArray().ToArray();
        encodings.Should().Contain(e => e.GetProperty("encoding").GetString() == "mapbox-style");
        var mapbox = encodings.Single(e => e.GetProperty("encoding").GetString() == "mapbox-style");
        // includeStylesheet inlines the canonical MapLibre body for the selected encoding.
        mapbox.GetProperty("inlineBody").GetString().Should().Contain("\"version\":8");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_style")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GetStyle_ByLayer_ResolvesLayerPrimaryStyle()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStylesForLayerAsync(StorageLayerId, Arg.Any<CancellationToken>())
            .Returns(new[] { Preset() });
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());

        var response = await DispatchAsync(
            GetStyleTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}} }""",
            catalog: catalog);

        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("structuredContent").GetProperty("styleId").GetString()
            .Should().Be(PresetStyleId);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_get_style")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_GetStyle_NoArguments_ListsAvailableStyles()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.ListStylesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Preset(), Preset("style_parcels", 1) });

        var response = await DispatchAsync(GetStyleTool.ToolName, "{}", catalog: catalog);

        response!.Error.Should().BeNull();
        var styles = response.Result!.Value.GetProperty("structuredContent")
            .GetProperty("styles").EnumerateArray().ToArray();
        styles.Should().HaveCount(2);
        styles.Select(s => s.GetProperty("styleId").GetString())
            .Should().BeEquivalentTo(PresetStyleId, "style_parcels");
        styles[0].GetProperty("uri").GetString().Should().Be($"honua://styles/{PresetStyleId}");
    }

    // ---------------------------------------------------------------
    // honua_apply_style_preset
    // ---------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_BindsPresetAndSyncsGraph()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        catalog.AssociateLayerAsync(StorageLayerId, PresetStyleId, 0, Arg.Any<CancellationToken>()).Returns(true);
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "{{PresetStyleId}}" }""",
            catalog: catalog,
            graphSync: graphSync);

        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        structured.GetProperty("styleId").GetString().Should().Be(PresetStyleId);
        structured.GetProperty("applied").GetBoolean().Should().BeTrue();
        structured.GetProperty("layerId").GetInt32().Should().Be(LayerIndex);

        // The preset was bound as the layer's primary style and the graph reconciled.
        await catalog.Received(1).AssociateLayerAsync(StorageLayerId, PresetStyleId, 0, Arg.Any<CancellationToken>());
        await graphSync.Received(1).SyncLayerStylesAsync(StorageLayerId, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_UnknownPreset_ReturnsInvalidArgumentNamingValidPresets()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync("style_missing", Arg.Any<CancellationToken>()).Returns((StyleCatalogRecord?)null);
        catalog.ListStylesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Preset(), Preset("style_parcels", 1) });
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "style_missing" }""",
            catalog: catalog,
            graphSync: graphSync);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should()
            .Contain("style_missing").And.Contain(PresetStyleId).And.Contain("style_parcels");

        // No binding or graph mutation happened for an unknown preset.
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_QueryOnlyPrincipal_ReturnsPermissionDenied()
    {
        // A query-only principal holds Read/Discover grants but not the
        // PublishedService.Publish authoring grant apply_style_preset requires.
        _jobService
            .EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(),
                OperatorResourceType.PublishedService,
                OperatorOperation.Publish,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new GeoprocessingAuthorizationException(requiresAuthentication: false)));

        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "{{PresetStyleId}}" }""",
            catalog: catalog,
            graphSync: graphSync);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("permission_denied");

        // Authorization is enforced before any style binding is written.
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(false, false, "permission_denied")]
    [InlineData(true, true, "failed_precondition")]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_GovernanceDenies_LeavesCatalogAndGraphUntouched(
        bool adminAuthorized, bool approvalRequired, string errorCode)
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();

        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "{{PresetStyleId}}" }""",
            catalog: catalog,
            graphSync: graphSync,
            adminAuthorized: adminAuthorized,
            approvalRequired: approvalRequired);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be(errorCode);
        if (approvalRequired)
        {
            result.GetProperty("structuredContent").GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        }
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(PolicyDecisionKind.Deny)]
    [InlineData(PolicyDecisionKind.RequireApproval)]
    [InlineData(PolicyDecisionKind.DryRunFirst)]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_OperationPolicyBlocks_LeavesCatalogAndGraphUntouched(
        PolicyDecisionKind policyDecision)
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var response = await DispatchAsync(
            ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "{{PresetStyleId}}" }""",
            catalog: catalog, graphSync: graphSync, policyDecision: policyDecision);

        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_ApprovalProposal_PreservesTargetWithoutActuation(bool dryRun)
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var bridge = Substitute.For<IOperationApprovalBridge>();
        bridge.CreateProposalAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<PolicyDecision>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var mapper = new StylePresetApprovalMapper();
                var gateway = mapper.Map(call.Arg<IOperationDescriptor>(), call.Arg<OperationRequest>(),
                    call.Arg<OperationPolicyContext>(), call.Arg<PolicyDecision>());
                gateway.Plan!.Summary.Should().Contain(ServiceId).And.Contain("layer '0'").And.Contain(PresetStyleId);
                gateway.Plan.Summary.Should().StartWith(dryRun ? "Preview" : "Apply");
                var replay = mapper.MapReplay(gateway);
                replay.Request.DryRun.Should().Be(dryRun);
                replay.Request.OperationId.Should().Be(StylePresetOperation.OperationId);
                replay.Request.Parameters["serviceId"].Should().Be(ServiceId);
                replay.Request.Parameters["layerId"].Should().Be("0");
                replay.Request.Parameters["styleId"].Should().Be(PresetStyleId);
                replay.Request.Parameters["expectedPublicationId"].Should().Be("pub-parcels");
                replay.Request.Parameters["expectedResourceId"].Should().Be(ResourceId);
                replay.Request.Parameters["expectedStorageBindingId"].Should().Be("bind-parcels");
                replay.Request.Parameters["expectedStorageLayerId"].Should().Be("42");
                call.Arg<OperationPolicyContext>().AuthorizationOutcome.Should().Be("authorized");
                return new OperationApprovalBridgeResult
                {
                    IsDurable = true,
                    ProposalId = "style-proposal",
                    AuditId = "style-audit",
                };
            });

        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{ "serviceId": "{{ServiceId}}", "layerId": {{LayerIndex}}, "styleId": "{{PresetStyleId}}", "dryRun": {{(dryRun ? "true" : "false")}} }""",
            catalog: catalog, graphSync: graphSync,
            policyDecision: PolicyDecisionKind.RequireApproval, approvalBridge: bridge);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        structured.GetProperty("proposalId").GetString().Should().Be("style-proposal");
        await bridge.Received(1).CreateProposalAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
            Arg.Any<OperationPolicyContext>(), Arg.Any<PolicyDecision>(), Arg.Any<CancellationToken>());
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_DryRunFirstPolicy_AllowsPreviewWithoutActuation()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"styleId":"{{PresetStyleId}}","dryRun":true}""",
            catalog: catalog, graphSync: graphSync, policyDecision: PolicyDecisionKind.DryRunFirst);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var output = result.GetProperty("structuredContent");
        output.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        output.GetProperty("applied").GetBoolean().Should().BeFalse();
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_MissingRuntime_ReturnsStructuredUnavailable(
        bool includeApprovalRuntime, bool includeOperationRuntime, bool includeGraphSyncRuntime)
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"styleId":"{{PresetStyleId}}"}""",
            catalog: catalog, graphSync: graphSync,
            includeApprovalRuntime: includeApprovalRuntime, includeOperationRuntime: includeOperationRuntime,
            includeGraphSyncRuntime: includeGraphSyncRuntime);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("unavailable");
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_PostCommitSyncFailure_ReportsAppliedWithWarning(bool cancelled)
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        catalog.AssociateLayerAsync(StorageLayerId, PresetStyleId, 0, Arg.Any<CancellationToken>()).Returns(true);
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        graphSync.SyncLayerStylesAsync(StorageLayerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(cancelled ? new OperationCanceledException() : new InvalidOperationException("projection failed")));

        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"styleId":"{{PresetStyleId}}"}""",
            catalog: catalog, graphSync: graphSync);
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var output = result.GetProperty("structuredContent");
        output.GetProperty("applied").GetBoolean().Should().BeTrue();
        output.GetProperty("warning").GetString().Should().Contain("reconciliation is pending");
        await graphSync.Received(1).SyncLayerStylesAsync(StorageLayerId, CancellationToken.None);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_AssociationNotApplied_DoesNotReportSuccessOrSync()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"styleId":"{{PresetStyleId}}"}""",
            catalog: catalog, graphSync: graphSync);
        response!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_EditionPolicy_RequiresApprovalBeforeActuation()
    {
        var license = Substitute.For<ILicenseEntitlementService>();
        license.GetSnapshot().Returns(new LicenseSnapshot(HonuaEdition.Pro, true,
            LicenseValidationState.Valid, null, null, null, null, [], new HashSet<string>(), 1, null));
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var bridge = Substitute.For<IOperationApprovalBridge>();
        bridge.CreateProposalAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<PolicyDecision>(), Arg.Any<CancellationToken>())
            .Returns(new OperationApprovalBridgeResult
            {
                IsDurable = true,
                ProposalId = "tier-style-proposal",
                AuditId = "tier-style-audit",
            });
        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"styleId":"{{PresetStyleId}}"}""",
            catalog: catalog, graphSync: graphSync, license: license, approvalBridge: bridge,
            policyOptions: new OperationPolicyOptions
            {
                Enabled = true,
                DefaultDecision = PolicyDecisionKind.Allow,
                Rules = [new OperationPolicyRule { OperationId = StylePresetOperation.OperationId,
                    Tier = "pro", Decision = PolicyDecisionKind.RequireApproval }],
            });
        response!.Result!.Value.GetProperty("structuredContent").GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        await bridge.Received(1).CreateProposalAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
            Arg.Is<OperationPolicyContext>(context => context.Tier == "pro"),
            Arg.Is<PolicyDecision>(decision => decision.Kind == PolicyDecisionKind.RequireApproval), Arg.Any<CancellationToken>());
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_ApplyStylePreset_DurableStoreUnavailable_PreservesDependencyReceipt()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"styleId":"{{PresetStyleId}}"}""",
            catalog: catalog, graphSync: graphSync, instanceStore: new UnavailableOperationInstanceStore());
        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var output = result.GetProperty("structuredContent");
        output.GetProperty("code").GetString().Should().Be("unavailable");
        output.GetProperty("missingDependency").GetString().Should().Be("redis");
        output.GetProperty("retryable").GetBoolean().Should().BeFalse();
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
        await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("publication")]
    [InlineData("storage")]
    [InlineData("missing-pin")]
    [InlineData("fallback-null")]
    [InlineData("fallback-invalid")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Update)]
    [Endpoint("POST /mcp tools/call honua_apply_style_preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ApprovedPresetReplay_RequiresTheOriginalTarget(string mutation)
    {
        var fallback = mutation.StartsWith("fallback-", StringComparison.Ordinal);
        var publicationBindingId = mutation == "fallback-invalid" ? "missing-binding" : null;
        var graph = BuildGraphProvider(useFallbackBinding: fallback, publicationBindingId: publicationBindingId);
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(PresetStyleId, Arg.Any<CancellationToken>()).Returns(Preset());
        catalog.AssociateLayerAsync(Arg.Any<int>(), PresetStyleId, 0, Arg.Any<CancellationToken>()).Returns(true);
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        OperationRequest? capturedReplay = null;
        var bridge = Substitute.For<IOperationApprovalBridge>();
        bridge.CreateProposalAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
                Arg.Any<OperationPolicyContext>(), Arg.Any<PolicyDecision>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var mapper = new StylePresetApprovalMapper();
                var gateway = mapper.Map(call.Arg<IOperationDescriptor>(), call.Arg<OperationRequest>(),
                    call.Arg<OperationPolicyContext>(), call.Arg<PolicyDecision>());
                capturedReplay = mapper.MapReplay(gateway).Request;
                return new OperationApprovalBridgeResult
                {
                    IsDurable = true,
                    ProposalId = "pinned-style-proposal",
                    AuditId = "pinned-style-audit",
                };
            });
        var response = await DispatchAsync(ApplyStylePresetTool.ToolName,
            $$"""{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}},"styleId":"{{PresetStyleId}}"}""",
            catalog: catalog, graphSync: graphSync, graphProvider: graph,
            policyDecision: PolicyDecisionKind.RequireApproval, approvalBridge: bridge);
        response!.Result!.Value.GetProperty("structuredContent").GetProperty("approvalRequired").GetBoolean().Should().BeTrue();
        var replay = capturedReplay ?? throw new InvalidOperationException("The proposal did not capture a replay request.");
        if (mutation is "publication" or "storage")
        {
            var rebound = BuildGraphProvider(storageLayerId: 77,
                resourceId: mutation == "publication" ? "res-rebound" : ResourceId,
                publicationId: mutation == "publication" ? "pub-rebound" : "pub-parcels",
                storageBindingId: "bind-rebound");
            graph.SetGraph((await rebound.GetCurrentAsync(CancellationToken.None)).Graph);
        }
        else if (fallback)
        {
            var rebound = BuildGraphProvider(storageBindingId: "bind-rebound",
                useFallbackBinding: true, publicationBindingId: publicationBindingId);
            graph.SetGraph((await rebound.GetCurrentAsync(CancellationToken.None)).Graph);
        }
        else if (mutation == "missing-pin")
        {
            replay = replay with
            {
                Parameters = replay.Parameters.Where(pair => !pair.Key.StartsWith("expected", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            };
        }

        using var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(graph)
            .AddSingleton(catalog)
            .AddSingleton(graphSync)
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        var executor = new StylePresetExecutor(services);
        var context = new OperationPolicyContext { ApprovedProposalId = "pinned-style-proposal" };
        async Task<OperationHandle> ReplayAsync()
        {
            var prepared = await executor.PrepareAsync(replay, context);
            return await executor.SubmitAsync(prepared, context);
        }

        if (mutation == "unchanged")
        {
            (await ReplayAsync()).Status.Should().Be(OperationHandleStatus.Completed);
            await catalog.Received(1).AssociateLayerAsync(StorageLayerId, PresetStyleId, 0, Arg.Any<CancellationToken>());
            await graphSync.Received(1).SyncLayerStylesAsync(StorageLayerId, CancellationToken.None);
        }
        else
        {
            Func<Task> act = async () => { _ = await ReplayAsync(); };
            await act.Should().ThrowAsync<ArgumentException>();
            await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
            await graphSync.DidNotReceiveWithAnyArgs().SyncLayerStylesAsync(default, default);
        }
    }

    [Theory]
    [InlineData("applied")]
    [InlineData("reconciliation-pending")]
    [InlineData("failed")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Update)]
    [Endpoint("POST /operations/style.apply-preset")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task StylePresetExecution_RecordsTargetAndOutcome(string outcome)
    {
        var recorded = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Honua",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "style.apply-preset.execute")
                {
                    recorded.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.AssociateLayerAsync(StorageLayerId, PresetStyleId, 0, Arg.Any<CancellationToken>())
            .Returns(outcome != "failed");
        var sync = Substitute.For<IMetadataV2StyleGraphSync>();
        if (outcome == "reconciliation-pending")
        {
            sync.SyncLayerStylesAsync(StorageLayerId, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("private failure details")));
        }
        using var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(BuildGraphProvider())
            .AddSingleton(catalog).AddSingleton(sync).AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        var executor = new StylePresetExecutor(services);
        var context = new OperationPolicyContext();
        var request = await executor.PrepareAsync(new OperationRequest
        {
            OperationId = StylePresetOperation.OperationId,
            Parameters = new Dictionary<string, string?>
            {
                ["serviceId"] = ServiceId, ["layerId"] = LayerIndex.ToString(), ["styleId"] = PresetStyleId,
            },
        }, context);
        if (outcome == "failed")
        {
            Func<Task> act = async () => { _ = await executor.SubmitAsync(request, context); };
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        else
        {
            (await executor.SubmitAsync(request, context)).Status.Should().Be(OperationHandleStatus.Completed);
        }
        var activity = recorded.Should().ContainSingle().Subject;
        activity.GetTagItem("service.id").Should().Be(ServiceId);
        activity.GetTagItem("layer.id").Should().Be(LayerIndex.ToString());
        activity.GetTagItem("style.id").Should().Be(PresetStyleId);
        activity.GetTagItem("storage.layer.id").Should().Be(StorageLayerId);
        activity.GetTagItem("operation.result").Should().Be(outcome);
        activity.Status.Should().Be(outcome == "applied" ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        if (outcome != "applied")
        {
            activity.GetTagItem("error.type").Should().Be(typeof(InvalidOperationException).FullName);
            activity.StatusDescription.Should().NotContain("private failure details");
        }
    }

    // ---------------------------------------------------------------
    // render reflects the applied style (mock-level)
    // ---------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_render_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_RenderMap_ReflectsAppliedStyleInCaption()
    {
        // After apply_style_preset, the layer's primary style resolves to the
        // preset; render_map surfaces it so the applied style is observable.
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStylesForLayerAsync(StorageLayerId, Arg.Any<CancellationToken>())
            .Returns(new[] { Preset() });

        var renderer = Substitute.For<IRasterMapRenderer>();
        renderer.RenderDatasetMapAsync(Arg.Any<int[]>(), Arg.Any<MapRenderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RasterResult
            {
                Data = Encoding.ASCII.GetBytes("PNGDATA"),
                ContentType = "image/png",
                Width = 256,
                Height = 256
            });

        var response = await DispatchAsync(
            RenderMapTool.ToolName,
            $$"""{ "layers":[{"serviceId":"{{ServiceId}}","layerId":{{LayerIndex}}}], "bbox":[-10,-10,10,10] }""",
            catalog: catalog,
            renderer: renderer);

        response!.Error.Should().BeNull();
        var content = response.Result!.Value.GetProperty("content").EnumerateArray().ToArray();
        var caption = content.First(b => b.GetProperty("type").GetString() == "text").GetProperty("text").GetString();
        caption.Should().Contain(PresetStyleId, "render_map reports each layer's effective (applied) style");
    }

    // ---------------------------------------------------------------
    // harness
    // ---------------------------------------------------------------

    private async Task<McpJsonRpcResponse?> DispatchAsync(
        string toolName,
        string argumentsJson,
        IStyleCatalog? catalog = null,
        IMetadataV2StyleGraphSync? graphSync = null,
        IRasterMapRenderer? renderer = null,
        bool adminAuthorized = true,
        bool approvalRequired = false,
        PolicyDecisionKind policyDecision = PolicyDecisionKind.Allow,
        IOperationApprovalBridge? approvalBridge = null,
        bool includeApprovalRuntime = true,
        bool includeOperationRuntime = true,
        ILicenseEntitlementService? license = null,
        OperationPolicyOptions? policyOptions = null,
        IOperationInstanceStore? instanceStore = null,
        TestMetadataV2GraphProvider? graphProvider = null,
        bool includeGraphSyncRuntime = true)
    {
        var surface = new McpDataAccessSurface(
            [
                new GetStyleTool(_jobService, NullLogger<GetStyleTool>.Instance),
                new ApplyStylePresetTool(_jobService, NullLogger<ApplyStylePresetTool>.Instance),
                new RenderMapTool(_jobService, NullLogger<RenderMapTool>.Instance),
            ],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var services = new ServiceCollection();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(adminAuthorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        services.AddSingleton(authorization);
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        approval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(approvalRequired ? ApprovalRequirement.Required("operator.publish") : ApprovalRequirement.NotRequired());
        services.AddSingleton(new OperatorApprovalGate(
            Substitute.For<IOperatorAuthorizationEvaluator>(), approval, NullLogger<OperatorApprovalGate>.Instance));
        services.AddSingleton<IMetadataV2GraphProvider>(graphProvider ?? BuildGraphProvider());
        services.AddSingleton(catalog ?? Substitute.For<IStyleCatalog>());
        services.AddSingleton(graphSync ?? Substitute.For<IMetadataV2StyleGraphSync>());
        services.AddSingleton(renderer ?? Substitute.For<IRasterMapRenderer>());

        // render_map's default result is an artifact reference stored through the
        // shared temp-file pipeline; stub it so the href path resolves in tests.
        var temporaryFileService = Substitute.For<ITemporaryFileService>();
        temporaryFileService
            .StoreTemporaryFileAsync(
                Arg.Any<byte[]>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<ClaimsPrincipal?>(),
                Arg.Any<CancellationToken>())
            .Returns("/temp/rendered-map.png");
        services.AddSingleton(temporaryFileService);

        if (license is not null)
        {
            services.AddSingleton(license);
        }
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOperationInvoker>(provider => new OperationDispatcher(
            new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System),
            [new StylePresetExecutor(provider)],
            new ConfigurableOperationPolicyDecisionPoint(Options.Create(policyOptions ?? new OperationPolicyOptions
            {
                Enabled = true,
                DefaultDecision = policyDecision,
            })),
            TimeProvider.System, approvalBridge: approvalBridge, instanceStore: instanceStore));

        if (!includeApprovalRuntime)
        {
            services.RemoveAll<OperatorApprovalGate>();
        }
        if (!includeOperationRuntime)
        {
            services.RemoveAll<IOperationInvoker>();
        }
        if (!includeGraphSyncRuntime)
        {
            services.RemoveAll<IMetadataV2StyleGraphSync>();
        }

        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services.BuildServiceProvider();

        return await surface.DispatchAsync(context, ToolCall("style-1", toolName, argumentsJson), CancellationToken.None);
    }

    private static TestMetadataV2GraphProvider BuildGraphProvider(
        int storageLayerId = StorageLayerId,
        string resourceId = ResourceId,
        string publicationId = "pub-parcels",
        string storageBindingId = "bind-parcels",
        bool useFallbackBinding = false, string? publicationBindingId = null)
    {
        var spatial = new MetadataV2ResourceSpatial
        {
            GeometryType = MetadataV2GeometryType.Polygon,
            SpatialReference = new MetadataV2SpatialReference { Srid = 4326 },
            Bbox = new MetadataV2Bbox { West = -10, South = -10, East = 10, North = 10 }
        };

        return new TestMetadataV2GraphBuilder()
            .AddResource(resourceId, "Parcels Dataset", spatial: spatial)
            .AddStorageBinding(storageBindingId, resourceId, "public.parcels", storageLayerId: storageLayerId)
            .AddService(ServiceId, ServiceName)
            .AddPublication(publicationId, ServiceId, resourceId, layerIndex: LayerIndex, storageBindingId: useFallbackBinding ? publicationBindingId : storageBindingId)
            .BuildProvider();
    }

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""
            {"name":"{{toolName}}","arguments":{{argumentsJson}}}
            """)
    };

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
