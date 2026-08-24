// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

        // 10. Propose publication — records intent only. This scenario has not
        // invoked honua_studio_save_version, so the item's
        // current/published pointers must both still be absent.
        var proposeTool = new ProposeStudioPublicationTool(jobService, NullLogger<ProposeStudioPublicationTool>.Instance);
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

        // The store creates an item record as a side effect of the very first
        // CreateDraftAsync (honua_studio_create_draft in step 1) — GetPointersAsync
        // is therefore non-null from that point on. What must stay true is that
        // NEITHER pointer was ever populated: this scenario never invoked the
        // explicit save-version tool, so both stay unset through every mutation
        // above, including propose-publication.
        var pointers = await store.GetPointersAsync(itemId);
        pointers.Should().NotBeNull("the item record exists once a draft has been created");
        pointers!.CurrentVersionId.Should().BeNull("the explicit save-version tool was not invoked");
        pointers.PublishedVersionId.Should().BeNull(
            "propose-publication must never create a content version or move the published pointer");
    }

    [Theory]
    [InlineData("map")]
    [InlineData("app")]
    [InlineData("dashboard")]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_save_version")]
    [Endpoint("POST /mcp tools/call honua_studio_get_version")]
    [Endpoint("POST /mcp tools/call honua_studio_reopen_version")]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    public async Task DurableVersionLifecycle_RoundTripsMapAppAndDashboard(string family)
    {
        using var provider = BuildServiceProvider();
        var lifecycleService = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycleService);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
        });

        var createTool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var createResult = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                $$$"""{"packageKey":"{{{family}}}-release-arc","family":"{{{family}}}","schemaVersion":"1.0","body":{"title":"{{{family}}} release arc"}}"""),
            CancellationToken.None);
        var created = createResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured create-draft content.");
        var originalDraftId = created.GetProperty("draftId").GetGuid();
        var generation = created.GetProperty("generation").GetInt64();

        var saveTool = new SaveStudioVersionTool(jobService, NullLogger<SaveStudioVersionTool>.Instance);
        var saveResult = await saveTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                $$$"""{"draftId":"{{{originalDraftId}}}","generation":{{{generation}}},"changeNote":"2026.1 E2E {{{family}}} save"}"""),
            CancellationToken.None);
        saveResult.IsError.Should().BeFalse();
        var saved = saveResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured save-version content.");
        var itemId = saved.GetProperty("itemId").GetGuid();
        var versionId = saved.GetProperty("versionId").GetGuid();
        var contentHash = saved.GetProperty("contentHash").GetString();
        saved.GetProperty("envelope").GetProperty("family").GetString().Should().Be(family);
        saved.GetProperty("sourceDraftId").GetGuid().Should().Be(originalDraftId);

        var getTool = new GetStudioVersionTool(jobService, NullLogger<GetStudioVersionTool>.Instance);
        var getResult = await getTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"itemId":"{{{itemId}}}","versionId":"{{{versionId}}}"}"""),
            CancellationToken.None);
        getResult.IsError.Should().BeFalse();
        var read = getResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured get-version content.");
        read.GetProperty("itemId").GetGuid().Should().Be(itemId);
        read.GetProperty("versionId").GetGuid().Should().Be(versionId);
        read.GetProperty("contentHash").GetString().Should().Be(contentHash);
        read.GetProperty("envelope").GetProperty("family").GetString().Should().Be(family);

        var reopenTool = new ReopenStudioVersionTool(jobService, NullLogger<ReopenStudioVersionTool>.Instance);
        var reopenResult = await reopenTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"itemId":"{{{itemId}}}","versionId":"{{{versionId}}}"}"""),
            CancellationToken.None);
        reopenResult.IsError.Should().BeFalse();
        var reopened = reopenResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured reopen-version content.");
        reopened.GetProperty("draftId").GetGuid().Should().NotBe(originalDraftId);
        reopened.GetProperty("itemId").GetGuid().Should().Be(itemId);
        reopened.GetProperty("baseVersionId").GetGuid().Should().Be(versionId);
        reopened.GetProperty("family").GetString().Should().Be(family);
        reopened.GetProperty("generation").GetInt64().Should().Be(1);

        var reopenedDraftId = reopened.GetProperty("draftId").GetGuid();
        var proposeTool = new ProposeStudioPublicationTool(
            jobService,
            NullLogger<ProposeStudioPublicationTool>.Instance);
        var proposeResult = await proposeTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                $$$"""{"draftId":"{{{reopenedDraftId}}}","generation":1,"route":"/studio/{{{family}}}-release-arc","visibility":"public","note":"ready for human approval"}"""),
            CancellationToken.None);
        proposeResult.IsError.Should().BeFalse();
        var proposal = proposeResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured propose-publication content.");
        proposal.GetProperty("recorded").GetBoolean().Should().BeTrue();
        proposal.GetProperty("humanConfirmationRequired").GetBoolean().Should().BeTrue();
        var proposedDraft = proposal.GetProperty("draft");
        proposedDraft.GetProperty("draftId").GetGuid().Should().Be(reopenedDraftId);
        proposedDraft.GetProperty("itemId").GetGuid().Should().Be(itemId);
        proposedDraft.GetProperty("family").GetString().Should().Be(family);
        proposedDraft.GetProperty("generation").GetInt64().Should().Be(2);
        proposedDraft.GetProperty("envelope").GetProperty("publicationIntent")
            .GetProperty("route").GetString().Should().Be($"/studio/{family}-release-arc");

        var pointers = await lifecycleService.GetPointersAsync(itemId);
        pointers!.CurrentVersionId.Should().Be(versionId,
            "recording intent on a reopened draft must not create a new version");
        pointers.PublishedVersionId.Should().BeNull(
            "an agent proposal must not expose any public route before human approval");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_save_version")]
    public async Task SaveVersion_WithStaleGeneration_FailsClosedWithoutMovingCurrentPointer()
    {
        using var provider = BuildServiceProvider();
        var lifecycleService = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycleService);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
        });

        var createTool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var created = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson("""{"packageKey":"stale-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);
        var draftId = created.StructuredContent!.Value.GetProperty("draftId").GetGuid();
        var itemId = created.StructuredContent!.Value.GetProperty("itemId").GetGuid();

        var updateTool = new UpdateStudioDraftTool(jobService, NullLogger<UpdateStudioDraftTool>.Instance);
        await updateTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                $$$"""{"draftId":"{{{draftId}}}","generation":1,"packageKey":"stale-map-updated","schemaVersion":"1.0"}"""),
            CancellationToken.None);

        var saveTool = new SaveStudioVersionTool(jobService, NullLogger<SaveStudioVersionTool>.Instance);
        var act = () => saveTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}","generation":1}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*Stale draft generation*");
        (await lifecycleService.ListVersionsAsync(itemId)).Should().BeEmpty();
        var pointers = await lifecycleService.GetPointersAsync(itemId);
        pointers!.CurrentVersionId.Should().BeNull();
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
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task OperatorBearer_CreateThenReadDraft_PreservesUpstreamIssuerOwnership()
    {
        using var provider = BuildServiceProvider();
        var lifecycleService = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var authorization = CreateEndUserAuthorizationService();
        var principal = await CreateOperatorBearerPrincipalAsync(
            "https://issuer-a.example",
            "shared-operator-subject");
        authorization.ResolveCallerId(principal).Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer-a.example:shared-operator-subject");

        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycleService);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            services.AddSingleton<IStudioAuthorizationService>(authorization);
        });
        httpContext.User = principal;

        var jobService = Substitute.For<IGeoprocessingJobService>();
        var createTool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var createResult = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                """{"packageKey":"operator-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);

        createResult.IsError.Should().BeFalse();
        var created = createResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured create-draft content.");
        created.GetProperty("ownerId").GetString().Should().Be(
            "oidc:subject:https%3A%2F%2Fissuer-a.example:shared-operator-subject");
        var draftId = created.GetProperty("draftId").GetGuid();

        var getTool = new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance);
        var getResult = await getTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""),
            CancellationToken.None);

        getResult.IsError.Should().BeFalse();
        getResult.StructuredContent!.Value.GetProperty("draftId").GetGuid().Should().Be(draftId);

        var saveTool = new SaveStudioVersionTool(jobService, NullLogger<SaveStudioVersionTool>.Instance);
        var saveResult = await saveTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                $$$"""{"draftId":"{{{draftId}}}","generation":1,"changeNote":"operator bearer save"}"""),
            CancellationToken.None);
        saveResult.IsError.Should().BeFalse();

        var proposeTool = new ProposeStudioPublicationTool(
            jobService,
            NullLogger<ProposeStudioPublicationTool>.Instance);
        var proposeResult = await proposeTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                $$$"""{"draftId":"{{{draftId}}}","generation":2,"route":"/studio/operator-map","visibility":"public"}"""),
            CancellationToken.None);
        proposeResult.IsError.Should().BeFalse();
        proposeResult.StructuredContent!.Value.GetProperty("recorded").GetBoolean().Should().BeTrue();

        // The same subject from a different validated upstream issuer remains a different
        // owner even though both credentials are wrapped by the same Honua bearer issuer.
        var otherIssuerContext = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycleService);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            services.AddSingleton<IStudioAuthorizationService>(authorization);
        });
        otherIssuerContext.User = await CreateOperatorBearerPrincipalAsync(
            "https://issuer-b.example",
            "shared-operator-subject");

        var crossIssuerRead = () => getTool.InvokeAsync(
            otherIssuerContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""),
            CancellationToken.None);
        await crossIssuerRead.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task SamlOperatorBearer_CreateThenReadDraft_PreservesIssuerOptionalOwnership()
    {
        using var provider = BuildServiceProvider();
        var lifecycleService = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var authorization = CreateEndUserAuthorizationService();
        var principal = await CreateSamlOperatorBearerPrincipalAsync("saml-operator-subject");
        authorization.ResolveCallerId(principal).Should().Be("saml:subject:-:saml-operator-subject");

        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycleService);
            services.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            services.AddSingleton<IStudioAuthorizationService>(authorization);
        });
        httpContext.User = principal;

        var jobService = Substitute.For<IGeoprocessingJobService>();
        var createTool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var createResult = await createTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson(
                """{"packageKey":"saml-operator-map","family":"map","schemaVersion":"1.0"}"""),
            CancellationToken.None);

        createResult.IsError.Should().BeFalse();
        var created = createResult.StructuredContent
            ?? throw new InvalidOperationException("Expected structured create-draft content.");
        created.GetProperty("ownerId").GetString().Should().Be("saml:subject:-:saml-operator-subject");
        var draftId = created.GetProperty("draftId").GetGuid();

        var getTool = new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance);
        var getResult = await getTool.InvokeAsync(
            httpContext,
            McpTestFactory.ParseJson($$$"""{"draftId":"{{{draftId}}}"}"""),
            CancellationToken.None);
        getResult.IsError.Should().BeFalse();
        getResult.StructuredContent!.Value.GetProperty("draftId").GetGuid().Should().Be(draftId);
    }

    private static StudioAuthorizationService CreateEndUserAuthorizationService()
    {
        var evaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
        evaluator.EvaluateAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<OperatorAuthorizationRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        return new StudioAuthorizationService(
            evaluator,
            new Honua.Core.Features.Authorization.OperatorScopeAuthorizer(),
            new StaticOptionsMonitor<StudioEndUserAuthorizationOptions>(
                new StudioEndUserAuthorizationOptions { Enabled = true }),
            new StaticOptionsMonitor<AdminRoleOptions>(new AdminRoleOptions()));
    }

    private static async Task<ClaimsPrincipal> CreateOperatorBearerPrincipalAsync(
        string upstreamIssuer,
        string subject)
    {
        var tokenService = new OperatorBearerTokenService(Options.Create(new OperatorBearerOptions
        {
            Enabled = true,
            SigningKey = "operator-bearer-studio-test-key-at-least-32-bytes-long",
            Issuer = "honua-operator-bearer",
            Audience = "honua-admin-api",
            MaxLifetimeMinutes = 10,
        }));
        var issuance = tokenService.Issue(
        [
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = subject },
            new AdminAuthSessionClaim { Type = "sub", Value = subject },
            new AdminAuthSessionClaim { Type = "iss", Value = upstreamIssuer },
            new AdminAuthSessionClaim { Type = "auth_type", Value = "oidc" },
            new AdminAuthSessionClaim { Type = IdentityProtocolProvenance.ClaimType, Value = IdentityProtocolProvenance.Oidc },
            new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "creator" },
        ],
        DateTimeOffset.UtcNow.AddMinutes(10));
        issuance.Should().NotBeNull();

        var projectedClaims = await tokenService.TryValidateAsync(issuance!.Token);
        projectedClaims.Should().NotBeNull();
        return AdminAuthClaimsProjector.CreatePrincipal(
            projectedClaims!,
            "OperatorBearer",
            "operator-bearer");
    }

    private static async Task<ClaimsPrincipal> CreateSamlOperatorBearerPrincipalAsync(string subject)
    {
        var tokenService = new OperatorBearerTokenService(Options.Create(new OperatorBearerOptions
        {
            Enabled = true,
            SigningKey = "operator-bearer-studio-test-key-at-least-32-bytes-long",
            Issuer = "honua-operator-bearer",
            Audience = "honua-admin-api",
            MaxLifetimeMinutes = 10,
        }));
        var issuance = tokenService.Issue(
        [
            new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = subject },
            new AdminAuthSessionClaim { Type = "sub", Value = subject },
            new AdminAuthSessionClaim { Type = "auth_type", Value = "saml" },
            new AdminAuthSessionClaim { Type = IdentityProtocolProvenance.ClaimType, Value = IdentityProtocolProvenance.Saml },
            new AdminAuthSessionClaim { Type = ClaimTypes.Role, Value = "creator" },
        ],
        DateTimeOffset.UtcNow.AddMinutes(10));
        issuance.Should().NotBeNull();

        var projectedClaims = await tokenService.TryValidateAsync(issuance!.Token);
        projectedClaims.Should().NotBeNull();
        return AdminAuthClaimsProjector.CreatePrincipal(
            projectedClaims!,
            "OperatorBearer",
            "operator-bearer");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose() { }
        }
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }
}
