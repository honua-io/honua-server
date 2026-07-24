// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Delegation tests for the Studio draft lifecycle and composition MCP tools
/// (honua-server#3002) against the real <see cref="IStudioPackageLifecycleService"/>
/// backed by <c>InMemoryStudioPackageStore</c> (mirrors
/// <c>StudioPackageLifecycleServiceTests</c>'s DI setup). Proves the tools
/// delegate to — and do not duplicate — the canonical lifecycle service: every
/// mutation observed through <c>honua_studio_get_draft</c> is the same state
/// the lifecycle service itself would report.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class StudioMcpToolDelegationTests
{
    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_*")]
    public async Task FullCompositionLifecycle_RoundTripsThroughTheRealLifecycleService()
    {
        var provider = BuildServiceProvider();
        var lifecycleService = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var store = provider.GetRequiredService<IStudioPackageStore>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var httpContext = McpTestFactory.AuthenticatedHttpContext();

        // 1. Create a map-family draft.
        var createTool = new CreateStudioDraftTool(lifecycleService, jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var createResult = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson("""{"packageKey":"parcels-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);
        createResult.IsError.Should().BeFalse();
        var draftId = createResult.StructuredContent!.Value.GetProperty("draftId").GetGuid();
        var itemId = createResult.StructuredContent.Value.GetProperty("itemId").GetGuid();
        createResult.StructuredContent.Value.GetProperty("generation").GetInt64().Should().Be(1);

        // 2. Add a layer.
        var addLayerTool = new AddStudioLayerTool(lifecycleService, jobService, NullLogger<AddStudioLayerTool>.Instance);
        var addLayerResult = await addLayerTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":1,"layer":{"id":"parcels","type":"fill","sourceId":"content.parcels"}}"""),
            CancellationToken.None);
        addLayerResult.IsError.Should().BeFalse();
        addLayerResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(2);

        // 3. Set the view.
        var setViewTool = new SetStudioViewTool(lifecycleService, jobService, NullLogger<SetStudioViewTool>.Instance);
        var setViewResult = await setViewTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":2,"view":{"center":[-157.86,21.31],"zoom":10}}"""),
            CancellationToken.None);
        setViewResult.IsError.Should().BeFalse();
        setViewResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(3);

        // 4. Add a widget.
        var addWidgetTool = new AddStudioWidgetTool(lifecycleService, jobService, NullLogger<AddStudioWidgetTool>.Instance);
        var addWidgetResult = await addWidgetTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":3,"widget":{"id":"legend","kind":"legend"}}"""),
            CancellationToken.None);
        addWidgetResult.IsError.Should().BeFalse();
        addWidgetResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(4);

        // 5. Set the layer's style.
        var setStyleTool = new SetStudioLayerStyleTool(lifecycleService, jobService, NullLogger<SetStudioLayerStyleTool>.Instance);
        var setStyleResult = await setStyleTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":4,"layerId":"parcels","styleRef":"style_parcels_default"}"""),
            CancellationToken.None);
        setStyleResult.IsError.Should().BeFalse();
        setStyleResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(5);

        // 6. Read the draft back — the same lifecycle-service state must show
        // the layer (with its style), view, and widget every tool call above wrote.
        var getTool = new GetStudioDraftTool(lifecycleService, jobService, NullLogger<GetStudioDraftTool>.Instance);
        var getResult = await getTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""),
            CancellationToken.None);
        getResult.IsError.Should().BeFalse();
        var draftFromLifecycleService = await lifecycleService.GetDraftAsync(draftId);
        draftFromLifecycleService.Should().NotBeNull();
        draftFromLifecycleService!.Generation.Should().Be(5);

        var body = StudioCompositionBodyEditor.ReadBody(draftFromLifecycleService.Envelope);
        body.Layers.Should().ContainSingle(l => l.Id == "parcels" && l.StyleRef == "style_parcels_default");
        body.View.Should().NotBeNull();
        body.View!.Zoom.Should().Be(10);
        body.Widgets.Should().ContainSingle(w => w.Id == "legend" && w.Kind == "legend");

        // 7. Remove the widget and the layer.
        var removeWidgetTool = new RemoveStudioWidgetTool(lifecycleService, jobService, NullLogger<RemoveStudioWidgetTool>.Instance);
        var removeWidgetResult = await removeWidgetTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":5,"widgetId":"legend"}"""),
            CancellationToken.None);
        removeWidgetResult.IsError.Should().BeFalse();

        var removeLayerTool = new RemoveStudioLayerTool(lifecycleService, jobService, NullLogger<RemoveStudioLayerTool>.Instance);
        var removeLayerResult = await removeLayerTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":6,"layerId":"parcels"}"""),
            CancellationToken.None);
        removeLayerResult.IsError.Should().BeFalse();
        var afterRemoval = await lifecycleService.GetDraftAsync(draftId);
        var bodyAfterRemoval = StudioCompositionBodyEditor.ReadBody(afterRemoval!.Envelope);
        bodyAfterRemoval.Layers.Should().BeEmpty();
        bodyAfterRemoval.Widgets.Should().BeEmpty();

        // 8. Whole-envelope update (rename the package key).
        var updateTool = new UpdateStudioDraftTool(lifecycleService, jobService, NullLogger<UpdateStudioDraftTool>.Instance);
        var updateResult = await updateTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":7,"packageKey":"parcels-map-v2","schemaVersion":"1.0"}"""),
            CancellationToken.None);
        updateResult.IsError.Should().BeFalse();
        updateResult.StructuredContent!.Value.GetProperty("packageKey").GetString().Should().Be("parcels-map-v2");

        // 9. Validate and preview.
        var validateTool = new ValidateStudioDraftTool(lifecycleService, jobService, NullLogger<ValidateStudioDraftTool>.Instance);
        var validateResult = await validateTool.InvokeAsync(
            httpContext, McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""), CancellationToken.None);
        validateResult.IsError.Should().BeFalse();

        var previewTool = new PreviewStudioDraftTool(lifecycleService, jobService, NullLogger<PreviewStudioDraftTool>.Instance);
        var previewResult = await previewTool.InvokeAsync(
            httpContext, McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""), CancellationToken.None);
        previewResult.IsError.Should().BeFalse();
        previewResult.StructuredContent!.Value.GetProperty("synchronous").GetBoolean().Should().BeTrue();

        // 10. Propose publication — records intent only. No content version was
        // ever created (no honua_studio_* tool creates one), so the item's
        // current/published pointers must both still be absent.
        var proposeTool = new ProposeStudioPublicationTool(lifecycleService, jobService, NullLogger<ProposeStudioPublicationTool>.Instance);
        var currentGeneration = (await lifecycleService.GetDraftAsync(draftId))!.Generation;
        var proposeResult = await proposeTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":{{{currentGeneration}}},"route":"/studio/parcels","visibility":"organization","note":"ready for review"}"""),
            CancellationToken.None);
        proposeResult.IsError.Should().BeFalse();
        proposeResult.StructuredContent!.Value.GetProperty("recorded").GetBoolean().Should().BeTrue();

        var finalDraft = await lifecycleService.GetDraftAsync(draftId);
        finalDraft!.Envelope.PublicationIntent.Should().NotBeNull();
        finalDraft.Envelope.PublicationIntent!.Route.Should().Be("/studio/parcels");
        finalDraft.Envelope.Provenance.Should().Contain(p => p.Rel == "proposes-publication");

        var pointers = await store.GetPointersAsync(itemId);
        pointers.Should().BeNull("propose-publication must never create a content version or move a current/published pointer");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_ThenGetDraft_ReturnsSameGenerationAndEnvelope()
    {
        var provider = BuildServiceProvider();
        var lifecycleService = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var httpContext = McpTestFactory.AuthenticatedHttpContext();

        var createTool = new CreateStudioDraftTool(lifecycleService, jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var createResult = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson("""{"packageKey":"apps-demo","family":"app","schemaVersion":"1.0","workspaceId":"studio"}"""),
            CancellationToken.None);
        var draftId = createResult.StructuredContent!.Value.GetProperty("draftId").GetGuid();

        var getTool = new GetStudioDraftTool(lifecycleService, jobService, NullLogger<GetStudioDraftTool>.Instance);
        var getResult = await getTool.InvokeAsync(
            httpContext, McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""), CancellationToken.None);

        getResult.IsError.Should().BeFalse();
        getResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(1);
        getResult.StructuredContent.Value.GetProperty("packageKey").GetString().Should().Be("apps-demo");
        getResult.StructuredContent.Value.GetProperty("family").GetString().Should().Be("app");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }
}
