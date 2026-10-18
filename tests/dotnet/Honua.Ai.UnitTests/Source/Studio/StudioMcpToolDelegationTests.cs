// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Styling.Abstractions;
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

    [UnitTest]
    public async Task InlineStyle_UsesCanonicalDraftGenerationAndSurvivesLaterCompositionEdit()
    {
        using var provider = BuildServiceProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });
        var create = new CreateStudioDraftTool(jobs, NullLogger<CreateStudioDraftTool>.Instance);
        var created = await create.InvokeAsync(context,
            McpTestFactory.ParseJson("""{"packageKey":"local-style-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);
        var draftId = created.StructuredContent!.Value.GetProperty("draftId").GetGuid();
        var add = new AddStudioLayerTool(jobs, NullLogger<AddStudioLayerTool>.Instance);
        await add.InvokeAsync(context,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":1,"layer":{"id":"parcels","type":"fill","sourceId":"content.parcels"}}"""),
            CancellationToken.None);
        var style = new SetStudioLayerStyleTool(jobs, NullLogger<SetStudioLayerStyleTool>.Instance);
        var arguments = McpTestFactory.ParseJson($$$$$"""
            {"draftId":"{{{{{draftId}}}}}","generation":2,"layerId":"parcels",
             "styleBody":{"parcels":{"paint":{"fill-color":"green"}}}}
            """);
        var result = await style.InvokeAsync(context, arguments, CancellationToken.None);
        result.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(3);

        // An old visual-editor snapshot cannot overwrite the intervening agent edit.
        var stale = () => style.InvokeAsync(context, arguments, CancellationToken.None);
        await stale.Should().ThrowAsync<StudioDraftGenerationConflictException>();
        var view = new SetStudioViewTool(jobs, NullLogger<SetStudioViewTool>.Instance);
        await view.InvokeAsync(context,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":3,"view":{"zoom":9}}"""),
            CancellationToken.None);
        var draft = await lifecycle.GetDraftAsync(draftId);
        var body = StudioCompositionBodyEditor.ReadBody(draft!.Envelope);
        draft.Generation.Should().Be(4);
        body.Layers.Single().StyleRef.Should().Be("honua-inline:parcels");
        body.StyleRefs!.Single().GetProperty("body").GetProperty("parcels")
            .GetProperty("paint").GetProperty("fill-color").GetString().Should().Be("green");
        body.View!.Zoom.Should().Be(9);
    }

    [UnitTest]
    public async Task FullMapStyle_UsesCanonicalGenerationAndRefusesValidationFailureWithoutMutation()
    {
        using var provider = BuildServiceProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var validator = new ControlledMapStyleValidator();
        var sources = new ControlledMapStyleSourceAuthorizer();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            services.AddSingleton<IMapStyleValidator>(validator);
            services.AddSingleton<IStudioMapStyleSourceAuthorizer>(sources);
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });
        var create = new CreateStudioDraftTool(jobs, NullLogger<CreateStudioDraftTool>.Instance);
        var created = await create.InvokeAsync(context, McpTestFactory.ParseJson("""
            {"packageKey":"full-style-map","family":"map","schemaVersion":"1.0","body":{
              "layers":[{"id":"parcels","type":"fill","sourceId":"parcels","styleRef":"shared"}],
              "mapSpec":{"version":8,"sources":{"parcels":{"type":"geojson","data":"/authorized/parcels"}},
                         "layers":[{"id":"parcels","type":"fill","source":"parcels"}]}}}
            """), CancellationToken.None);
        var draftId = created.StructuredContent!.Value.GetProperty("draftId").GetGuid();
        var tool = new SetStudioMapStyleTool(jobs, NullLogger<SetStudioMapStyleTool>.Instance);
        var arguments = McpTestFactory.ParseJson($$$"""
            {"draftId":"{{{draftId}}}","generation":1,"mapSpec":{
              "version":8,"sources":{"parcels":{"type":"geojson","data":"/authorized/parcels"}},
              "layers":[{"id":"parcels","type":"fill","source":"parcels","paint":{"fill-color":"green"}}]}}
            """);
        validator.Reject = true;
        var invalid = () => tool.InvokeAsync(context, arguments, CancellationToken.None);
        await invalid.Should().ThrowAsync<GeoprocessingValidationException>();
        (await lifecycle.GetDraftAsync(draftId))!.Generation.Should().Be(1);

        validator.Reject = false;
        sources.Reject = true;
        var forbidden = () => tool.InvokeAsync(context, arguments, CancellationToken.None);
        await forbidden.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        (await lifecycle.GetDraftAsync(draftId))!.Generation.Should().Be(1);
        sources.Reject = false;
        await tool.InvokeAsync(context, arguments, CancellationToken.None);
        var stale = () => tool.InvokeAsync(context, arguments, CancellationToken.None);
        await stale.Should().ThrowAsync<StudioDraftGenerationConflictException>();
        var draft = (await lifecycle.GetDraftAsync(draftId))!;
        var body = StudioCompositionBodyEditor.ReadBody(draft.Envelope);
        draft.Generation.Should().Be(2);
        body.Layers.Single().StyleRef.Should().BeNull();
        body.StyleRefs.Should().BeEmpty();
        body.MapSpec!.Value.GetProperty("layers")[0].GetProperty("paint")
            .GetProperty("fill-color").GetString().Should().Be("green");
    }

    [UnitTest]
    public async Task WholeDraftStyleUpdate_RechecksAccessAndValidation_AndPersistsCanonicalBindings()
    {
        using var provider = BuildServiceProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var validator = new ControlledMapStyleValidator();
        var sources = new ControlledMapStyleSourceAuthorizer();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            services.AddSingleton<IMapStyleValidator>(validator);
            services.AddSingleton<IStudioMapStyleSourceAuthorizer>(sources);
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });
        const string bodyJson = """
            {"mapPackageId":"whole-style","format":"honua_map_package.v1",
             "layers":[{"id":"parcels","type":"fill","sourceId":"parcels","styleRef":"shared"}],
             "widgets":[],"sourceBindings":[{"sourceId":"parcels","protocol":"geoservices_feature_service",
               "locator":{"url":"https://wrong.invalid/FeatureServer/9","layerId":9}}],
             "mapSpec":{"version":8,"sources":{"parcels":{"type":"geojson","data":"/authorized/parcels"}},
               "layers":[{"id":"parcels","type":"fill","source":"parcels"}]}}
            """;
        var create = new CreateStudioDraftTool(jobs, NullLogger<CreateStudioDraftTool>.Instance);
        var created = await create.InvokeAsync(context, McpTestFactory.ParseJson($$$"""
            {"packageKey":"whole-style","family":"map","schemaVersion":"1.0","body":{{{bodyJson}}}}
            """), CancellationToken.None);
        var draftId = created.StructuredContent!.Value.GetProperty("draftId").GetGuid();
        var body = System.Text.Json.Nodes.JsonNode.Parse(bodyJson)!.AsObject();
        body["mapSpec"]!["layers"]![0]!["paint"] = new System.Text.Json.Nodes.JsonObject
        {
            ["fill-color"] = "green"
        };
        var update = new UpdateStudioDraftTool(jobs, NullLogger<UpdateStudioDraftTool>.Instance);
        Task<Honua.Ai.Protocols.Mcp.Models.McpToolsCallResult> Invoke() => update.InvokeAsync(context,
            McpTestFactory.ParseJson($$$"""
                {"draftId":"{{{draftId}}}","generation":1,"packageKey":"whole-style",
                 "schemaVersion":"1.0","body":{{{body.ToJsonString()}}}}
                """), CancellationToken.None);

        sources.Reject = true;
        await ((Func<Task>)(async () => await Invoke())).Should().ThrowAsync<GeoprocessingAuthorizationException>();
        sources.Reject = false;
        validator.Reject = true;
        await ((Func<Task>)(async () => await Invoke())).Should().ThrowAsync<GeoprocessingValidationException>();
        validator.Reject = false;
        body["mapSpec"]!["sources"]!["parcels"]!["data"] = "https://wrong.invalid/private";
        await ((Func<Task>)(async () => await Invoke())).Should().ThrowAsync<GeoprocessingValidationException>();
        (await lifecycle.GetDraftAsync(draftId))!.Generation.Should().Be(1);

        body["mapSpec"]!["sources"]!["parcels"]!["data"] = "/authorized/parcels";
        var response = await Invoke();
        response.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(2);
        var saved = (await lifecycle.GetDraftAsync(draftId))!;
        var result = StudioCompositionBodyEditor.ReadBody(saved.Envelope);
        result.Layers.Single().StyleRef.Should().BeNull();
        result.MapSpec!.Value.GetProperty("layers")[0].GetProperty("paint")
            .GetProperty("fill-color").GetString().Should().Be("green");
        result.SourceBindings!.Single().GetProperty("locator").GetProperty("url")
            .GetString().Should().Be("/authorized/parcels");
        await ((Func<Task>)(async () => await Invoke())).Should().ThrowAsync<StudioDraftGenerationConflictException>();
    }

    private sealed class ControlledMapStyleValidator : IMapStyleValidator
    {
        public bool Reject { get; set; }

        public bool TryValidate(System.Text.Json.JsonElement style, out string? error)
        {
            error = Reject ? "Unsupported style property." : null;
            return !Reject;
        }
    }

    private sealed class ControlledMapStyleSourceAuthorizer : IStudioMapStyleSourceAuthorizer
    {
        public bool Reject { get; set; }

        public Task<StudioCompositionBody> AuthorizeStyleReferencesAsync(System.Security.Claims.ClaimsPrincipal principal,
            StudioCompositionBody body, CancellationToken cancellationToken) => Task.FromResult(body);

        public Task<StudioCompositionBody> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal principal,
            StudioCompositionBody body, CancellationToken cancellationToken)
        {
            if (Reject) throw new UnauthorizedAccessException("Source access was revoked.");
            return Task.FromResult(body with
            {
                MapSpec = body.MapSpec ?? McpTestFactory.ParseJson("""
                    {"version":8,"sources":{"parcels":{"type":"geojson","data":"/authorized/parcels"}},
                     "layers":[{"id":"parcels","type":"fill","source":"parcels"}]}
                    """),
                SourceBindings = [McpTestFactory.ParseJson("""
                    {"sourceId":"parcels","protocol":"geoservices_feature_service",
                     "locator":{"url":"/authorized/parcels","layerId":0}}
                    """)],
                SourceBindingIds = new HashSet<string>(StringComparer.Ordinal) { "parcels" }
            });
        }
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }
}
