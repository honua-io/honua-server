// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Tool tests for <c>honua_studio_bind_interaction</c> / <c>honua_studio_remove_interaction</c>
/// — Honua's reference implementation of the geospatial-mcp <c>composition</c>-profile
/// tools <c>bind_interaction</c>/<c>remove_interaction</c> (ADR-0030). Mirrors the
/// existing composition-tool suites: the happy paths run against the real
/// <see cref="IStudioPackageLifecycleService"/> backed by <c>InMemoryStudioPackageStore</c>
/// (as <see cref="StudioMcpToolDelegationTests"/> does), and the typed-error paths use a
/// substituted lifecycle service (as <see cref="StudioMcpToolContractTests"/> does).
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class StudioInteractionMcpToolTests
{
    private static readonly Guid MockDraftId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string BindSelectParcel =
        """
        {
          "id": "select-parcel-filters-chart",
          "on": { "ref": "layer:parcels", "event": "featureSelect" },
          "do": { "ref": "widget:area-chart", "verb": "setFilter", "args": { "field": "parcelId", "value": "$event.featureId" } }
        }
        """;

    private readonly IStudioPackageLifecycleService _lifecycleService = Substitute.For<IStudioPackageLifecycleService>();
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_AddsTheBindingToTheDraftComposition()
    {
        var harness = await StudioDraftHarness.CreateAsync();

        var result = await harness.BindAsync(BindSelectParcel);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(harness.Generation);

        var body = await harness.ReadCompositionAsync();
        var interaction = body.Interactions.Should().ContainSingle().Subject;
        interaction.Id.Should().Be("select-parcel-filters-chart");
        interaction.On.Ref.Should().Be("layer:parcels");
        interaction.On.Event.Should().Be("featureSelect");
        interaction.Do.Ref.Should().Be("widget:area-chart");
        interaction.Do.Verb.Should().Be("setFilter");
        interaction.Do.Args!.Value.GetProperty("value").GetString().Should().Be("$event.featureId");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WithAnExistingId_ReplacesRatherThanAppends()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.BindAsync(BindSelectParcel);
        await harness.BindAsync(
            """
            {
              "id": "hover-highlights",
              "on": { "ref": "layer:parcels", "event": "featureHover" },
              "do": { "ref": "layer:parcels", "verb": "setVisibility" }
            }
            """);

        await harness.BindAsync(
            """
            {
              "id": "select-parcel-filters-chart",
              "on": { "ref": "layer:parcels", "event": "featureSelect" },
              "do": { "ref": "map", "verb": "setViewport", "args": { "bbox": "$event.bbox" } }
            }
            """);

        var body = await harness.ReadCompositionAsync();
        body.Interactions.Should().HaveCount(2);
        body.Interactions![0].Id.Should().Be("select-parcel-filters-chart");
        body.Interactions[0].Do.Verb.Should().Be("setViewport");
        body.Interactions[1].Id.Should().Be("hover-highlights");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_interaction")]
    public async Task RemoveInteraction_WithAnExistingId_RemovesOnlyThatBinding()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.BindAsync(BindSelectParcel);
        await harness.BindAsync(
            """
            {
              "id": "hover-highlights",
              "on": { "ref": "layer:parcels", "event": "featureHover" },
              "do": { "ref": "layer:parcels", "verb": "setVisibility" }
            }
            """);

        var result = await harness.RemoveAsync("select-parcel-filters-chart");

        result.IsError.Should().BeFalse();
        var body = await harness.ReadCompositionAsync();
        body.Interactions!.Select(i => i.Id).Should().Equal("hover-highlights");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_interaction")]
    public async Task RemoveInteraction_WithAnUnknownId_SurfacesNotFound()
    {
        // ADR-0030: removing an unknown id is an ERROR, not a no-op, so an agent cannot
        // believe it unbound something it never bound.
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.BindAsync(BindSelectParcel);

        var act = () => harness.RemoveAsync("never-bound");

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_SurvivesUnrelatedCompositionMutations()
    {
        // The WriteBody projection-overlay regression, exercised end-to-end through the
        // tool surface: an authored interactions block must survive later add-layer /
        // set-view / add-widget tool calls.
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.BindAsync(BindSelectParcel);

        await harness.InvokeAsync(
            new AddStudioLayerTool(harness.JobService, NullLogger<AddStudioLayerTool>.Instance),
            g => $$$"""{"draftId":"{{{harness.DraftId}}}","generation":{{{g}}},"layer":{"id":"zoning","type":"fill"}}""");
        await harness.InvokeAsync(
            new SetStudioViewTool(harness.JobService, NullLogger<SetStudioViewTool>.Instance),
            g => $$$"""{"draftId":"{{{harness.DraftId}}}","generation":{{{g}}},"view":{"zoom":11}}""");
        await harness.InvokeAsync(
            new AddStudioWidgetTool(harness.JobService, NullLogger<AddStudioWidgetTool>.Instance),
            g => $$$"""{"draftId":"{{{harness.DraftId}}}","generation":{{{g}}},"widget":{"id":"legend","kind":"legend"}}""");

        var body = await harness.ReadCompositionAsync();
        body.Interactions.Should().ContainSingle(i => i.Id == "select-parcel-filters-chart");
        body.Layers.Select(l => l.Id).Should().Equal("parcels", "zoning");
        body.Widgets.Select(w => w.Id).Should().Equal("area-chart", "legend");
        body.View!.Zoom.Should().Be(11);
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WithAControlRef_ResolvesOnceTheControlIsAdded()
    {
        // End-to-end inverse of the pre-ADR-0031 behaviour: the same bind that used to be
        // rejected outright now succeeds against a draft whose controls collection declares
        // the control, and still fails while it does not.
        var harness = await StudioDraftHarness.CreateAsync();
        const string bind =
            """
            {
              "id": "year-filters-parcels",
              "on": { "ref": "control:year-slider", "event": "change" },
              "do": { "ref": "layer:parcels", "verb": "setFilter" }
            }
            """;

        var beforeControl = () => harness.BindAsync(bind);
        var error = await beforeControl.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("does not resolve");

        await harness.AddControlAsync("""{"id":"year-slider","kind":"timeSlider","sourceId":"parcels"}""");
        var result = await harness.BindAsync(bind);

        result.IsError.Should().BeFalse();
        var body = await harness.ReadCompositionAsync();
        body.Interactions.Should().ContainSingle().Which.On.Ref.Should().Be("control:year-slider");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WithAnUnresolvableRef_SurfacesInvalidArgument()
    {
        var harness = await StudioDraftHarness.CreateAsync();

        var act = () => harness.BindAsync(
            """
            {
              "id": "i1",
              "on": { "ref": "layer:not-composed", "event": "featureSelect" },
              "do": { "ref": "map", "verb": "setViewport" }
            }
            """);

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("does not resolve");
    }

    [Theory]
    [InlineData("featureDoubleClick", "setFilter")]
    [InlineData("featureSelect", "setBasemap")]
    public async Task BindInteraction_OutsideTheClosedEventOrVerbSets_SurfacesInvalidArgument(
        string eventName, string verb)
    {
        // MCP dispatch does not evaluate the advertised inputSchema, so the closed sets are
        // enforced in the HANDLER — this fails even though the schema also states them.
        var harness = await StudioDraftHarness.CreateAsync();

        var act = () => harness.BindAsync(
            $$"""
            {
              "id": "i1",
              "on": { "ref": "layer:parcels", "event": "{{eventName}}" },
              "do": { "ref": "map", "verb": "{{verb}}" }
            }
            """);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WithOversizedId_SurfacesInvalidArgument()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        var id = new string('i', StudioInteractionVocabulary.MaxInteractionIdLength + 1);

        var act = () => harness.BindAsync(
            $$"""
            {
              "id": "{{id}}",
              "on": { "ref": "layer:parcels", "event": "featureSelect" },
              "do": { "ref": "map", "verb": "setViewport" }
            }
            """);

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("characters or fewer");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_PastTheFanOutCap_SurfacesInvalidArgument()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        for (var i = 0; i < StudioInteractionVocabulary.MaxInteractionsPerEventSource; i++)
        {
            await harness.BindAsync(
                $$"""
                {
                  "id": "binding-{{i}}",
                  "on": { "ref": "layer:parcels", "event": "featureSelect" },
                  "do": { "ref": "map", "verb": "setViewport" }
                }
                """);
        }

        var act = () => harness.BindAsync(
            """
            {
              "id": "binding-over-cap",
              "on": { "ref": "layer:parcels", "event": "featureSelect" },
              "do": { "ref": "map", "verb": "setViewport" }
            }
            """);

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("may share the same event source");

        // Rejected, not truncated: the stored document still holds exactly the cap.
        var body = await harness.ReadCompositionAsync();
        body.Interactions.Should().HaveCount(StudioInteractionVocabulary.MaxInteractionsPerEventSource);
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WhenGenerationIsStale_SurfacesTypedFailedPrecondition()
    {
        var draft = MockDraft(generation: 1);
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(MockDraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StudioPackageDraft?>(
                new InvalidOperationException("Stale draft generation; refresh and retry.")));

        var tool = new BindStudioInteractionTool(_jobService, NullLogger<BindStudioInteractionTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(),
            McpTestFactory.ParseJson(
                $$"""{"draftId":"{{MockDraftId}}","generation":1,"interaction":{{BindSelectParcel}}}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_interaction")]
    public async Task RemoveInteraction_WhenGenerationIsStale_SurfacesTypedFailedPrecondition()
    {
        var draft = MockDraft(generation: 1, withInteraction: true);
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(MockDraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StudioPackageDraft?>(
                new InvalidOperationException("Stale draft generation; refresh and retry.")));

        var tool = new RemoveStudioInteractionTool(_jobService, NullLogger<RemoveStudioInteractionTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(),
            McpTestFactory.ParseJson(
                $$"""{"draftId":"{{MockDraftId}}","generation":1,"interactionId":"select-parcel-filters-chart"}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WhenDraftFamilyIsNotMapOrApp_SurfacesInvalidArgument()
    {
        var draft = MockDraft(generation: 1) with { Family = StudioPackageFamily.Query };
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new BindStudioInteractionTool(_jobService, NullLogger<BindStudioInteractionTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(),
            McpTestFactory.ParseJson(
                $$"""{"draftId":"{{MockDraftId}}","generation":1,"interaction":{{BindSelectParcel}}}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [Theory]
    [InlineData("""{"draftId":"22222222-2222-2222-2222-222222222222","generation":1}""")]
    [InlineData("""{"draftId":"22222222-2222-2222-2222-222222222222","generation":1,"interaction":{"id":"i1"}}""")]
    [InlineData("""{"draftId":"22222222-2222-2222-2222-222222222222","generation":1,"interaction":{"on":{"ref":"map","event":"viewportChange"},"do":{"ref":"map","verb":"setViewport"}}}""")]
    [InlineData("""{"generation":1,"interaction":{"id":"i1","on":{"ref":"map","event":"viewportChange"},"do":{"ref":"map","verb":"setViewport"}}}""")]
    [InlineData("""{"draftId":"22222222-2222-2222-2222-222222222222","interaction":{"id":"i1","on":{"ref":"map","event":"viewportChange"},"do":{"ref":"map","verb":"setViewport"}}}""")]
    public async Task BindInteraction_WithIncompleteArguments_SurfacesInvalidArgument(string argumentsJson)
    {
        var draft = MockDraft(generation: 1);
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new BindStudioInteractionTool(_jobService, NullLogger<BindStudioInteractionTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(), McpTestFactory.ParseJson(argumentsJson), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WithExplicitNullArgs_SurfacesInvalidArgument()
    {
        var harness = await StudioDraftHarness.CreateAsync();

        var act = () => harness.BindAsync(
            """
            {
              "id": "i1",
              "on": { "ref": "layer:parcels", "event": "featureSelect" },
              "do": { "ref": "map", "verb": "setViewport", "args": null }
            }
            """);

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("must be a JSON object");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public async Task BindInteraction_WithExplicitNullDisabled_SurfacesInvalidArgument()
    {
        var harness = await StudioDraftHarness.CreateAsync();

        var act = () => harness.BindAsync(
            """
            {
              "id": "i1",
              "on": { "ref": "layer:parcels", "event": "featureSelect" },
              "do": { "ref": "map", "verb": "setViewport" },
              "disabled": null
            }
            """);

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("must be a JSON boolean");
    }

    [Theory]
    [InlineData("\"disable\":true")]
    [InlineData("\"on\":{\"ref\":\"layer:parcels\",\"event\":\"featureSelect\",\"once\":true}")]
    [InlineData("\"do\":{\"ref\":\"map\",\"verb\":\"setViewport\",\"argz\":{}}")]
    public async Task BindInteraction_WithUnknownMember_SurfacesInvalidArgument(string replacementMember)
    {
        var harness = await StudioDraftHarness.CreateAsync();
        var on = replacementMember.StartsWith("\"on\"", StringComparison.Ordinal)
            ? replacementMember
            : "\"on\":{\"ref\":\"layer:parcels\",\"event\":\"featureSelect\"}";
        var action = replacementMember.StartsWith("\"do\"", StringComparison.Ordinal)
            ? replacementMember
            : "\"do\":{\"ref\":\"map\",\"verb\":\"setViewport\"}";
        var extra = replacementMember.StartsWith("\"on\"", StringComparison.Ordinal)
            || replacementMember.StartsWith("\"do\"", StringComparison.Ordinal)
                ? string.Empty
                : $",{replacementMember}";

        var act = () => harness.BindAsync($$"""{"id":"i1",{{on}},{{action}}{{extra}}}""");

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_interaction")]
    public async Task RemoveInteraction_WithOversizedId_SurfacesInvalidArgument()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        var interactionId = new string('i', StudioInteractionVocabulary.MaxInteractionIdLength + 1);

        var act = () => harness.RemoveAsync(interactionId);

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("characters or fewer");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_bind_interaction")]
    public void InteractionTools_AdvertiseTheAdr0030Contract()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var bind = new BindStudioInteractionTool(jobService, NullLogger<BindStudioInteractionTool>.Instance).Describe();
        var remove = new RemoveStudioInteractionTool(jobService, NullLogger<RemoveStudioInteractionTool>.Instance).Describe();

        bind.Name.Should().Be("honua_studio_bind_interaction");
        remove.Name.Should().Be("honua_studio_remove_interaction");

        foreach (var descriptor in new[] { bind, remove })
        {
            descriptor.InputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
            descriptor.InputSchema.GetProperty("required").EnumerateArray()
                .Select(e => e.GetString()).Should().Contain(["draftId", "generation"]);
            // Presentation wiring only: neither tool is a mutation-profile tool.
            descriptor.Annotations!.ReadOnlyHint.Should().BeFalse();
        }

        bind.Annotations!.IdempotentHint.Should().BeTrue();
        remove.Annotations!.DestructiveHint.Should().BeTrue();
    }

    private Microsoft.AspNetCore.Http.DefaultHttpContext MockedHttpContext() =>
        McpTestFactory.AuthenticatedHttpContextWithServices(services => services.AddSingleton(_lifecycleService));

    private static StudioPackageDraft MockDraft(long generation, bool withInteraction = false)
    {
        var interactions = withInteraction
            ? $""","interactions":[{BindSelectParcel}]"""
            : string.Empty;
        using var body = JsonDocument.Parse(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }]{{interactions}}
            }
            """);

        var now = DateTimeOffset.UnixEpoch;
        return new StudioPackageDraft
        {
            DraftId = MockDraftId,
            ItemId = Guid.NewGuid(),
            PackageKey = "parcels",
            Family = StudioPackageFamily.Map,
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Body = body.RootElement.Clone(),
            },
            Generation = generation,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// A real (in-memory-store-backed) map draft pre-composed with the <c>parcels</c>
    /// layer and <c>area-chart</c> widget every interaction fixture references, plus the
    /// generation bookkeeping each optimistic-concurrency call needs.
    /// </summary>
    private sealed class StudioDraftHarness
    {
        private readonly IStudioPackageLifecycleService _lifecycle;
        private readonly Microsoft.AspNetCore.Http.DefaultHttpContext _httpContext;

        private StudioDraftHarness(
            IStudioPackageLifecycleService lifecycle,
            Microsoft.AspNetCore.Http.DefaultHttpContext httpContext,
            IGeoprocessingJobService jobService,
            Guid draftId,
            long generation)
        {
            _lifecycle = lifecycle;
            _httpContext = httpContext;
            JobService = jobService;
            DraftId = draftId;
            Generation = generation;
        }

        public IGeoprocessingJobService JobService { get; }

        public Guid DraftId { get; }

        public long Generation { get; private set; }

        public static async Task<StudioDraftHarness> CreateAsync()
        {
            var services = new ServiceCollection();
            services.AddStudioPackageLifecycle();
            var provider = services.BuildServiceProvider();
            var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
            var jobService = Substitute.For<IGeoprocessingJobService>();
            var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(registrations =>
            {
                registrations.AddSingleton(lifecycle);
                registrations.AddSingleton(provider.GetRequiredService<IStudioPackageValidator>());
            });

            var createTool = new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance);
            var created = await createTool.InvokeAsync(
                httpContext,
                McpTestFactory.ParseJson("""{"packageKey":"parcels-map","family":"map","schemaVersion":"1.0"}"""),
                CancellationToken.None);
            var content = created.StructuredContent!.Value;
            var harness = new StudioDraftHarness(
                lifecycle, httpContext, jobService, content.GetProperty("draftId").GetGuid(),
                content.GetProperty("generation").GetInt64());

            await harness.InvokeAsync(
                new AddStudioLayerTool(jobService, NullLogger<AddStudioLayerTool>.Instance),
                g => $$$"""{"draftId":"{{{harness.DraftId}}}","generation":{{{g}}},"layer":{"id":"parcels","type":"fill"}}""");
            await harness.InvokeAsync(
                new AddStudioWidgetTool(jobService, NullLogger<AddStudioWidgetTool>.Instance),
                g => $$$"""{"draftId":"{{{harness.DraftId}}}","generation":{{{g}}},"widget":{"id":"area-chart","kind":"chart"}}""");
            return harness;
        }

        public Task<McpToolsCallResult> BindAsync(string interactionJson) => InvokeAsync(
            new BindStudioInteractionTool(JobService, NullLogger<BindStudioInteractionTool>.Instance),
            g => $$"""{"draftId":"{{DraftId}}","generation":{{g}},"interaction":{{interactionJson}}}""");

        public Task<McpToolsCallResult> AddControlAsync(string controlJson) => InvokeAsync(
            new AddStudioControlTool(JobService, NullLogger<AddStudioControlTool>.Instance),
            g => $$"""{"draftId":"{{DraftId}}","generation":{{g}},"control":{{controlJson}}}""");

        public Task<McpToolsCallResult> RemoveAsync(string interactionId) => InvokeAsync(
            new RemoveStudioInteractionTool(JobService, NullLogger<RemoveStudioInteractionTool>.Instance),
            g => $$"""{"draftId":"{{DraftId}}","generation":{{g}},"interactionId":"{{interactionId}}"}""");

        public async Task<McpToolsCallResult> InvokeAsync(IMcpTool tool, Func<long, string> arguments)
        {
            var result = await tool.InvokeAsync(
                _httpContext, McpTestFactory.ParseJson(arguments(Generation)), CancellationToken.None);
            if (result.StructuredContent is { } content &&
                content.TryGetProperty("generation", out var generation))
            {
                Generation = generation.GetInt64();
            }

            return result;
        }

        public async Task<StudioCompositionBody> ReadCompositionAsync()
        {
            var draft = await _lifecycle.GetDraftAsync(DraftId);
            return StudioCompositionBodyEditor.ReadBody(draft!.Envelope);
        }
    }
}
