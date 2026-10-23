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
        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycleService);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });

        // 1. Create a map-family draft.
        var createTool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var createResult = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson("""{"packageKey":"parcels-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);
        createResult.IsError.Should().BeFalse();
        var createContent = createResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured MCP content.");
        createContent.GetProperty("operation").GetProperty("operationInstanceId").GetString()
            .Should().StartWith("opinst-");
        var draftId = createContent.GetProperty("draftId").GetGuid();
        var itemId = createContent.GetProperty("itemId").GetGuid();
        createContent.GetProperty("generation").GetInt64().Should().Be(1);

        // 2. Add a layer.
        var addLayerTool = new AddStudioLayerTool(jobService, NullLogger<AddStudioLayerTool>.Instance);
        var addLayerResult = await addLayerTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":1,"layer":{"id":"parcels","type":"fill","sourceId":"content.parcels"}}"""),
            CancellationToken.None);
        addLayerResult.IsError.Should().BeFalse();
        addLayerResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(2);

        // 3. Set the view.
        var setViewTool = new SetStudioViewTool(jobService, NullLogger<SetStudioViewTool>.Instance);
        var setViewResult = await setViewTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":2,"view":{"center":[-157.86,21.31],"zoom":10}}"""),
            CancellationToken.None);
        setViewResult.IsError.Should().BeFalse();
        setViewResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(3);

        // 4. Add a widget.
        var addWidgetTool = new AddStudioWidgetTool(jobService, NullLogger<AddStudioWidgetTool>.Instance);
        var addWidgetResult = await addWidgetTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":3,"widget":{"id":"legend","kind":"legend"}}"""),
            CancellationToken.None);
        addWidgetResult.IsError.Should().BeFalse();
        addWidgetResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(4);

        // 5. Set the layer's style.
        var setStyleTool = new SetStudioLayerStyleTool(jobService, NullLogger<SetStudioLayerStyleTool>.Instance);
        var setStyleResult = await setStyleTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":4,"layerId":"parcels","styleRef":"style_parcels_default"}"""),
            CancellationToken.None);
        setStyleResult.IsError.Should().BeFalse();
        setStyleResult.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(5);

        // 6. Read the draft back — the same lifecycle-service state must show
        // the layer (with its style), view, and widget every tool call above wrote.
        var getTool = new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance);
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
        var removeWidgetTool = new RemoveStudioWidgetTool(jobService, NullLogger<RemoveStudioWidgetTool>.Instance);
        var removeWidgetResult = await removeWidgetTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":5,"widgetId":"legend"}"""),
            CancellationToken.None);
        removeWidgetResult.IsError.Should().BeFalse();

        var removeLayerTool = new RemoveStudioLayerTool(jobService, NullLogger<RemoveStudioLayerTool>.Instance);
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
        var updateTool = new UpdateStudioDraftTool(jobService, NullLogger<UpdateStudioDraftTool>.Instance);
        var updateResult = await updateTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":7,"packageKey":"parcels-map-v2","schemaVersion":"1.0"}"""),
            CancellationToken.None);
        updateResult.IsError.Should().BeFalse();
        updateResult.StructuredContent!.Value.GetProperty("packageKey").GetString().Should().Be("parcels-map-v2");

        // 9. Validate and preview.
        var validateTool = new ValidateStudioDraftTool(jobService, NullLogger<ValidateStudioDraftTool>.Instance);
        var validateResult = await validateTool.InvokeAsync(
            httpContext, McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""), CancellationToken.None);
        validateResult.IsError.Should().BeFalse();

        var previewTool = new PreviewStudioDraftTool(jobService, NullLogger<PreviewStudioDraftTool>.Instance);
        var previewResult = await previewTool.InvokeAsync(
            httpContext, McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""), CancellationToken.None);
        previewResult.IsError.Should().BeFalse();
        previewResult.StructuredContent!.Value.GetProperty("synchronous").GetBoolean().Should().BeTrue();

        // 10. Save an immutable version through the lifecycle service before
        // proposing publication. Composition alone must not populate either pointer.
        var pointersBeforeSave = await store.GetPointersAsync(itemId);
        pointersBeforeSave.Should().NotBeNull("the item record exists once a draft has been created");
        pointersBeforeSave!.CurrentVersionId.Should().BeNull();
        pointersBeforeSave.PublishedVersionId.Should().BeNull();

        var currentGeneration = (await lifecycleService.GetDraftAsync(draftId))!.Generation;
        var version = await lifecycleService.SaveDraftAsVersionAsync(
            draftId, "ready for review", "test-user", currentGeneration);
        version.Should().NotBeNull();
        var draftBeforeProposal = await lifecycleService.GetDraftAsync(draftId);

        // Publication delegates the saved identity to the approval runtime;
        // it must neither mutate the draft nor publish the saved version.
        var proposeTool = new ProposeStudioPublicationTool(jobService, NullLogger<ProposeStudioPublicationTool>.Instance);
        var proposeResult = await proposeTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"itemId":"{{{itemId}}}","versionId":"{{{version!.VersionId}}}","contentHash":"{{{version.ContentHash}}}","route":"/studio/parcels","visibility":"organization","note":"ready for review"}"""),
            CancellationToken.None);
        proposeResult.IsError.Should().BeFalse();
        var proposal = proposeResult.StructuredContent!.Value;
        proposal.GetProperty("status").GetString().Should().Be("AwaitingApproval");
        proposal.GetProperty("humanConfirmationRequired").GetBoolean().Should().BeTrue();
        proposal.GetProperty("proposalUri").GetString().Should().Be("honua://proposals/proposal-studio-publication");
        proposal.GetProperty("operation").GetProperty("operationId").GetString()
            .Should().Be("studio.content.create-publication-request");

        var finalDraft = await lifecycleService.GetDraftAsync(draftId);
        finalDraft.Should().BeEquivalentTo(draftBeforeProposal);

        var pointers = await store.GetPointersAsync(itemId);
        pointers.Should().NotBeNull();
        pointers!.CurrentVersionId.Should().Be(version.VersionId,
            "propose-publication must keep the exact saved version current");
        pointers.PublishedVersionId.Should().BeNull(
            "propose-publication must never create a content version or move the published pointer");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_ThenGetDraft_ReturnsSameGenerationAndEnvelope()
    {
        var provider = BuildServiceProvider();
        var lifecycleService = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycleService);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });

        var createTool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var createResult = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson("""{"packageKey":"apps-demo","family":"app","schemaVersion":"1.0","workspaceId":"studio"}"""),
            CancellationToken.None);
        var draftId = createResult.StructuredContent!.Value.GetProperty("draftId").GetGuid();

        var getTool = new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance);
        var getResult = await getTool.InvokeAsync(
            httpContext, McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""), CancellationToken.None);

        getResult.IsError.Should().BeFalse();
        var getContent = getResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured MCP content.");
        getContent.GetProperty("generation").GetInt64().Should().Be(1);
        getContent.GetProperty("packageKey").GetString().Should().Be("apps-demo");
        getContent.GetProperty("family").GetString().Should().Be("app");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }
}
