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
/// Tool tests for <c>honua_studio_add_control</c> / <c>honua_studio_remove_control</c>
/// — Honua's reference implementation of the geospatial-mcp <c>composition</c>-profile
/// tools <c>add_control</c>/<c>remove_control</c> (ADR-0031). Mirrors
/// <c>StudioInteractionMcpToolTests</c>: happy paths run against the real
/// <see cref="IStudioPackageLifecycleService"/> backed by <c>InMemoryStudioPackageStore</c>,
/// and the typed-error paths use a substituted lifecycle service.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class StudioControlMcpToolTests
{
    private static readonly Guid MockDraftId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string YearSlider =
        """
        {
          "id": "year-slider",
          "kind": "timeSlider",
          "title": "Year built",
          "sourceId": "parcels",
          "config": { "field": "yearBuilt" }
        }
        """;

    private readonly IStudioPackageLifecycleService _lifecycleService = Substitute.For<IStudioPackageLifecycleService>();
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_AddsTheControlToTheDraftComposition()
    {
        var harness = await StudioDraftHarness.CreateAsync();

        var result = await harness.AddAsync(YearSlider);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(harness.Generation);

        var body = await harness.ReadCompositionAsync();
        var control = body.Controls.Should().ContainSingle().Subject;
        control.Id.Should().Be("year-slider");
        control.Kind.Should().Be("timeSlider");
        control.Title.Should().Be("Year built");
        control.SourceId.Should().Be("parcels");
        control.Config!.Value.GetProperty("field").GetString().Should().Be("yearBuilt");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_WithAnExistingId_ReplacesRatherThanAppends()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.AddAsync(YearSlider);
        await harness.AddAsync("""{"id":"basemap-picker","kind":"basemapSwitcher"}""");

        await harness.AddAsync("""{"id":"year-slider","kind":"filterSlider","title":"Year"}""");

        var body = await harness.ReadCompositionAsync();
        body.Controls.Should().HaveCount(2);
        body.Controls![0].Id.Should().Be("year-slider");
        body.Controls[0].Kind.Should().Be("filterSlider");
        body.Controls[1].Id.Should().Be("basemap-picker");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_control")]
    public async Task RemoveControl_WithAnExistingId_RemovesOnlyThatControl()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.AddAsync(YearSlider);
        await harness.AddAsync("""{"id":"basemap-picker","kind":"basemapSwitcher"}""");

        var result = await harness.RemoveAsync("year-slider");

        result.IsError.Should().BeFalse();
        var body = await harness.ReadCompositionAsync();
        body.Controls!.Select(c => c.Id).Should().Equal("basemap-picker");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_control")]
    public async Task RemoveControl_WithoutCascade_RejectsWhileAnInteractionStillReferencesTheControl()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.AddAsync(YearSlider);
        await harness.BindAsync(
            """
            {
              "id": "year-filters-parcels",
              "on": { "ref": "control:year-slider", "event": "change" },
              "do": { "ref": "layer:parcels", "verb": "setFilter" }
            }
            """);

        var act = () => harness.RemoveAsync("year-slider");

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("cascadeInteractions=true");

        // Rejected, not partially applied: both the control and its binding survive.
        var body = await harness.ReadCompositionAsync();
        body.Controls.Should().ContainSingle();
        body.Interactions.Should().ContainSingle();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_control")]
    public async Task RemoveControl_WithCascade_RemovesTheControlAndItsBindings()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.AddAsync(YearSlider);
        await harness.BindAsync(
            """
            {
              "id": "year-filters-parcels",
              "on": { "ref": "control:year-slider", "event": "change" },
              "do": { "ref": "layer:parcels", "verb": "setFilter" }
            }
            """);
        await harness.BindAsync(
            """
            {
              "id": "hover-highlights",
              "on": { "ref": "layer:parcels", "event": "featureHover" },
              "do": { "ref": "layer:parcels", "verb": "setVisibility" }
            }
            """);

        var result = await harness.RemoveAsync("year-slider", cascadeInteractions: true);

        result.IsError.Should().BeFalse();
        var body = await harness.ReadCompositionAsync();
        body.Controls.Should().BeEmpty();
        body.Interactions!.Select(i => i.Id).Should().Equal("hover-highlights");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_control")]
    public async Task RemoveControl_WithUnknownId_SurfacesNotFound()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.AddAsync(YearSlider);

        var act = () => harness.RemoveAsync("never-added");

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [Theory]
    [InlineData("draw")]
    [InlineData("edit")]
    [InlineData("legend")]
    public async Task AddControl_OutsideTheClosedKindVocabulary_SurfacesInvalidArgument(string kind)
    {
        // MCP dispatch does not evaluate the advertised inputSchema, so the closed set is
        // enforced in the HANDLER. `draw`/`edit` are the load-bearing rejections: ADR-0031
        // admits no feature-editing control (ADR-0028 keeps source mutation governed).
        var harness = await StudioDraftHarness.CreateAsync();

        var act = () => harness.AddAsync($$"""{"id":"c1","kind":"{{kind}}"}""");

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("must be one of");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_WithAnUnresolvableSourceId_SurfacesInvalidArgument()
    {
        // ADR-0031 makes source resolution a validation-gate responsibility, and the
        // advertised schema says so — an agent that misspells the layer must be told at
        // authoring time, not ship a control whose domain no host can populate.
        var harness = await StudioDraftHarness.CreateAsync();

        var act = () => harness.AddAsync("""{"id":"f","kind":"filterSelect","sourceId":"parcel"}""");

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("does not resolve");

        // The draft is unchanged: rejected at admission, not persisted.
        var body = await harness.ReadCompositionAsync();
        body.Controls.Should().BeNullOrEmpty();

        // The correctly spelled layer is accepted.
        (await harness.AddAsync("""{"id":"f","kind":"filterSelect","sourceId":"parcels"}""")).IsError.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_WithOversizedId_SurfacesInvalidArgument()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        var id = new string('c', StudioInteractionVocabulary.MaxControlIdLength + 1);

        var act = () => harness.AddAsync($$"""{"id":"{{id}}","kind":"navigation"}""");

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("characters or fewer");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_WithOversizedTitle_SurfacesInvalidArgumentWithoutPersisting()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        var title = new string('t', StudioInteractionVocabulary.MaxControlTitleLength + 1);

        var act = () => harness.AddAsync(
            $$"""{"id":"navigation","kind":"navigation","title":"{{title}}"}""");

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("control.title");
        (await harness.ReadCompositionAsync()).Controls.Should().BeNullOrEmpty();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_WithOversizedSourceId_SurfacesInvalidArgumentWithoutPersisting()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        var sourceId = new string('s', StudioInteractionVocabulary.MaxControlSourceIdLength + 1);

        var act = () => harness.AddAsync(
            $$"""{"id":"filter","kind":"filterSelect","sourceId":"{{sourceId}}"}""");

        var error = await act.Should().ThrowAsync<GeoprocessingValidationException>();
        error.Which.Message.Should().Contain("control.sourceId");
        (await harness.ReadCompositionAsync()).Controls.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("""{"draftId":"33333333-3333-3333-3333-333333333333","generation":1}""")]
    [InlineData("""{"draftId":"33333333-3333-3333-3333-333333333333","generation":1,"control":{"kind":"navigation"}}""")]
    [InlineData("""{"draftId":"33333333-3333-3333-3333-333333333333","generation":1,"control":{"id":"nav"}}""")]
    [InlineData("""{"draftId":"33333333-3333-3333-3333-333333333333","generation":1,"control":{"id":"nav","kind":"navigation","position":"top-right"}}""")]
    [InlineData("""{"generation":1,"control":{"id":"nav","kind":"navigation"}}""")]
    [InlineData("""{"draftId":"33333333-3333-3333-3333-333333333333","control":{"id":"nav","kind":"navigation"}}""")]
    public async Task AddControl_WithIncompleteOrUnknownArguments_SurfacesInvalidArgument(string argumentsJson)
    {
        var draft = MockDraft(generation: 1);
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new AddStudioControlTool(_jobService, NullLogger<AddStudioControlTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(), McpTestFactory.ParseJson(argumentsJson), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_WhenGenerationIsStale_SurfacesTypedFailedPrecondition()
    {
        var draft = MockDraft(generation: 1);
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(MockDraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StudioPackageDraft?>(
                new InvalidOperationException("Stale draft generation; refresh and retry.")));

        var tool = new AddStudioControlTool(_jobService, NullLogger<AddStudioControlTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(),
            McpTestFactory.ParseJson($$"""{"draftId":"{{MockDraftId}}","generation":1,"control":{{YearSlider}}}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_control")]
    public async Task RemoveControl_WhenGenerationIsStale_SurfacesTypedFailedPrecondition()
    {
        var draft = MockDraft(generation: 1, withControl: true);
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(MockDraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StudioPackageDraft?>(
                new InvalidOperationException("Stale draft generation; refresh and retry.")));

        var tool = new RemoveStudioControlTool(_jobService, NullLogger<RemoveStudioControlTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(),
            McpTestFactory.ParseJson($$"""{"draftId":"{{MockDraftId}}","generation":1,"controlId":"year-slider"}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_WhenDraftFamilyIsNotMapOrApp_SurfacesInvalidArgument()
    {
        var draft = MockDraft(generation: 1) with { Family = StudioPackageFamily.Query };
        _lifecycleService.GetDraftAsync(MockDraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new AddStudioControlTool(_jobService, NullLogger<AddStudioControlTool>.Instance);
        var act = () => tool.InvokeAsync(
            MockedHttpContext(),
            McpTestFactory.ParseJson($$"""{"draftId":"{{MockDraftId}}","generation":1,"control":{{YearSlider}}}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public async Task AddControl_PreservesControlsAcrossUnrelatedCompositionMutations()
    {
        var harness = await StudioDraftHarness.CreateAsync();
        await harness.AddAsync(YearSlider);

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
        body.Controls.Should().ContainSingle(c => c.Id == "year-slider");
        body.Layers.Select(l => l.Id).Should().Equal("parcels", "zoning");
        body.Widgets.Select(w => w.Id).Should().Equal("area-chart", "legend");
        body.View!.Zoom.Should().Be(11);
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_control")]
    public void ControlTools_AdvertiseTheAdr0031Contract()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var add = new AddStudioControlTool(jobService, NullLogger<AddStudioControlTool>.Instance).Describe();
        var remove = new RemoveStudioControlTool(jobService, NullLogger<RemoveStudioControlTool>.Instance).Describe();

        add.Name.Should().Be("honua_studio_add_control");
        remove.Name.Should().Be("honua_studio_remove_control");

        foreach (var descriptor in new[] { add, remove })
        {
            descriptor.InputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
            descriptor.InputSchema.GetProperty("required").EnumerateArray()
                .Select(e => e.GetString()).Should().Contain(["draftId", "generation"]);
            // Presentation wiring only: neither tool is a mutation-profile tool.
            descriptor.Annotations!.ReadOnlyHint.Should().BeFalse();
        }

        add.Annotations!.IdempotentHint.Should().BeTrue();
        remove.Annotations!.DestructiveHint.Should().BeTrue();

        // The advertised kind enum is the closed ADR-0031 vocabulary, rendered from the
        // same domain contract the handler enforces.
        add.InputSchema.GetProperty("properties").GetProperty("control").GetProperty("properties")
            .GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(StudioInteractionVocabulary.ControlKinds);

        remove.InputSchema.GetProperty("properties").TryGetProperty("cascadeInteractions", out var cascade)
            .Should().BeTrue();
        cascade.GetProperty("default").GetBoolean().Should().BeFalse();
    }

    private Microsoft.AspNetCore.Http.DefaultHttpContext MockedHttpContext() =>
        McpTestFactory.AuthenticatedHttpContextWithServices(services => services.AddSingleton(_lifecycleService));

    private static StudioPackageDraft MockDraft(long generation, bool withControl = false)
    {
        var controls = withControl ? $""","controls":[{YearSlider}]""" : string.Empty;
        using var body = JsonDocument.Parse(
            $$"""
            {
              "format": "honua_map_package.v1",
              "layers": [{ "id": "parcels" }],
              "widgets": [{ "id": "area-chart", "kind": "chart" }]{{controls}}
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
    /// A real (in-memory-store-backed) map draft pre-composed with the <c>parcels</c> layer
    /// and <c>area-chart</c> widget the control fixtures bind against, plus the generation
    /// bookkeeping each optimistic-concurrency call needs.
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

        public Task<McpToolsCallResult> AddAsync(string controlJson) => InvokeAsync(
            new AddStudioControlTool(JobService, NullLogger<AddStudioControlTool>.Instance),
            g => $$"""{"draftId":"{{DraftId}}","generation":{{g}},"control":{{controlJson}}}""");

        public Task<McpToolsCallResult> RemoveAsync(string controlId, bool cascadeInteractions = false) => InvokeAsync(
            new RemoveStudioControlTool(JobService, NullLogger<RemoveStudioControlTool>.Instance),
            g => $$"""
                {"draftId":"{{DraftId}}","generation":{{g}},"controlId":"{{controlId}}","cascadeInteractions":{{(cascadeInteractions ? "true" : "false")}}}
                """);

        public Task<McpToolsCallResult> BindAsync(string interactionJson) => InvokeAsync(
            new BindStudioInteractionTool(JobService, NullLogger<BindStudioInteractionTool>.Instance),
            g => $$"""{"draftId":"{{DraftId}}","generation":{{g}},"interaction":{{interactionJson}}}""");

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
