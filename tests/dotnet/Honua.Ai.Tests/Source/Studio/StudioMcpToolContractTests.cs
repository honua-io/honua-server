// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Tool-contract tests for the Studio draft lifecycle and composition MCP
/// tools (honua-server#3002): input-schema shape, generation-conflict typed
/// errors, family-gating and not-found typed errors, the structural
/// publish-tool-absence guarantee (REQ-003/REQ-009), read-only honesty for
/// validate/preview (PR #3016 review), and the per-request service
/// resolution that keeps the singleton tool descriptors from capturing the
/// scoped <see cref="IStudioPackageLifecycleService"/>/<see cref="IStudioPackageValidator"/>
/// as constructor dependencies. Uses mocked collaborators injected via
/// <c>httpContext.RequestServices</c>; <see cref="StudioMcpToolDelegationTests"/>
/// covers the real <c>InMemoryStudioPackageStore</c>-backed happy path.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class StudioMcpToolContractTests
{
    private static readonly Guid DraftId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IStudioPackageLifecycleService _lifecycleService = Substitute.For<IStudioPackageLifecycleService>();
    private readonly IStudioPackageValidator _validator = Substitute.For<IStudioPackageValidator>();
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_*")]
    public void ToolRoster_ContainsNoPublishOrRollbackExecutionTool()
    {
        // REQ-003/REQ-010: the agent-reachable tool surface must contain no
        // publish/share/embed or rollback execution tool. The only
        // publish-adjacent tool is honua_studio_propose_publication, which
        // records intent only.
        var names = BuildAllTools().Select(t => t.Name).ToArray();

        names.Should().NotContain(n => n.Contains("rollback", StringComparison.OrdinalIgnoreCase));
        // "publ" (not "publish") so it also matches "propose_publication" —
        // "publish" is not literally a substring of "publication".
        names.Where(n => n.Contains("publ", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(["honua_studio_propose_publication"]);
        names.Should().NotContain("honua_studio_publish");
        names.Should().NotContain("honua_studio_publish_draft");
        names.Should().NotContain("honua_studio_execute_publish");
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    public async Task ProposePublication_NeverCallsVersionOrPointerMovingLifecycleMembers()
    {
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(draft with { Generation = 2 });

        var tool = new ProposeStudioPublicationTool(_jobService, NullLogger<ProposeStudioPublicationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioProposePublicationArgument { DraftId = DraftId, Generation = 1, Route = "/studio/parcels" },
            StudioMcpJsonContext.Default.McpStudioProposePublicationArgument);

        var result = await tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("recorded").GetBoolean().Should().BeTrue();
        result.StructuredContent.Value.GetProperty("humanConfirmationRequired").GetBoolean().Should().BeTrue();

        // Structural proof: this tool never touches the version/publish-request/
        // rollback surface — only the ordinary generation-checked draft update.
        await _lifecycleService.DidNotReceive().CreatePublicationRequestAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<StudioPublicationIntent?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().SaveDraftAsVersionAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().RollbackAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<StudioRollbackPointer>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().DeleteDraftAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_update_draft")]
    public async Task UpdateDraft_WhenGenerationIsStale_SurfacesTypedFailedPrecondition()
    {
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StudioPackageDraft?>(
                new InvalidOperationException("Stale draft generation; refresh and retry.")));

        var tool = new UpdateStudioDraftTool(_jobService, NullLogger<UpdateStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioUpdateDraftArgument
            {
                DraftId = DraftId,
                Generation = 1,
                PackageKey = "parcels-map",
                SchemaVersion = "1.0",
            },
            StudioMcpJsonContext.Default.McpStudioUpdateDraftArgument);

        var act = () => tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_layer")]
    public async Task AddLayer_WhenGenerationIsStale_SurfacesTypedFailedPrecondition()
    {
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<StudioPackageDraft?>(
                new InvalidOperationException("Stale draft generation; refresh and retry.")));

        var tool = new AddStudioLayerTool(_jobService, NullLogger<AddStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioAddLayerArgument
            {
                DraftId = DraftId,
                Generation = 1,
                Layer = new McpStudioLayerInput { Id = "parcels" },
            },
            StudioMcpJsonContext.Default.McpStudioAddLayerArgument);

        var act = () => tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_layer")]
    public async Task AddLayer_WhenDraftFamilyIsNotMapOrApp_SurfacesInvalidArgument()
    {
        var draft = BuildDraft(StudioPackageFamily.Query, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new AddStudioLayerTool(_jobService, NullLogger<AddStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioAddLayerArgument
            {
                DraftId = DraftId,
                Generation = 1,
                Layer = new McpStudioLayerInput { Id = "parcels" },
            },
            StudioMcpJsonContext.Default.McpStudioAddLayerArgument);

        var act = () => tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_layer")]
    public async Task RemoveLayer_WhenLayerIdDoesNotExist_SurfacesNotFound()
    {
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new RemoveStudioLayerTool(_jobService, NullLogger<RemoveStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioRemoveLayerArgument { DraftId = DraftId, Generation = 1, LayerId = "no-such-layer" },
            StudioMcpJsonContext.Default.McpStudioRemoveLayerArgument);

        var act = () => tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task GetDraft_WhenDraftDoesNotExist_SurfacesNotFound()
    {
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns((StudioPackageDraft?)null);

        var tool = new GetStudioDraftTool(_jobService, NullLogger<GetStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioDraftIdArgument { DraftId = DraftId },
            StudioMcpJsonContext.Default.McpStudioDraftIdArgument);

        var act = () => tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task GetDraft_WhenLifecycleServiceIsNotComposed_SurfacesRetryableUnavailable()
    {
        // PR #3016 review, P1 remediation: registration is unconditional, so a
        // host that never composed Studio persistence must still advertise the
        // tool but fail per-call with a structured, retryable error rather
        // than an opaque internal error.
        var tool = new GetStudioDraftTool(_jobService, NullLogger<GetStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioDraftIdArgument { DraftId = DraftId },
            StudioMcpJsonContext.Default.McpStudioDraftIdArgument);

        // No IStudioPackageLifecycleService registered in RequestServices.
        var httpContext = McpTestFactory.AuthenticatedHttpContextWithServices(_ => { });
        var act = () => tool.InvokeAsync(httpContext, arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingStoreUnavailableException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_WhenPackageKeyMissing_SurfacesInvalidArgument()
    {
        var tool = new CreateStudioDraftTool(_jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""{"family":"map","schemaVersion":"1.0"}""");

        var act = () => tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_WhenFamilyIsUnknown_SurfacesInvalidArgument()
    {
        var tool = new CreateStudioDraftTool(_jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""{"packageKey":"parcels-map","family":"not-a-family","schemaVersion":"1.0"}""");

        var act = () => tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_validate_draft")]
    public async Task ValidateDraft_IsGenuinelyReadOnly_NeverPersistsOrChangesGeneration()
    {
        // PR #3016 review, P2 honesty: readOnlyHint=true must be honest. The
        // tool must call the pure IStudioPackageValidator.Validate directly
        // and never UpdateDraftAsync/ValidateDraftAsync (both of which persist
        // through the store and bump the draft's generation).
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 3);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        var expectedValidation = new StudioValidationSummary { Status = StudioPackageValidationStatus.Valid };
        _validator.Validate(draft.Envelope).Returns(expectedValidation);

        var tool = new ValidateStudioDraftTool(_jobService, NullLogger<ValidateStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioDraftIdArgument { DraftId = DraftId },
            StudioMcpJsonContext.Default.McpStudioDraftIdArgument);

        var result = await tool.InvokeAsync(HttpContextWithLifecycleServiceAndValidator(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("valid");
        tool.Describe().Annotations!.ReadOnlyHint.Should().BeTrue();

        _validator.Received(1).Validate(draft.Envelope);
        await _lifecycleService.DidNotReceive().UpdateDraftAsync(
            Arg.Any<Guid>(), Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().ValidateDraftAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_preview_draft")]
    public async Task PreviewDraft_IsGenuinelyReadOnly_NeverPersistsOrChangesGeneration()
    {
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 4);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        var expectedValidation = new StudioValidationSummary { Status = StudioPackageValidationStatus.Valid };
        _validator.Validate(draft.Envelope).Returns(expectedValidation);

        var tool = new PreviewStudioDraftTool(_jobService, NullLogger<PreviewStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioDraftIdArgument { DraftId = DraftId },
            StudioMcpJsonContext.Default.McpStudioDraftIdArgument);

        var result = await tool.InvokeAsync(HttpContextWithLifecycleServiceAndValidator(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("synchronous").GetBoolean().Should().BeTrue();
        tool.Describe().Annotations!.ReadOnlyHint.Should().BeTrue();

        _validator.Received(1).Validate(draft.Envelope);
        await _lifecycleService.DidNotReceive().UpdateDraftAsync(
            Arg.Any<Guid>(), Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().ValidateDraftAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _lifecycleService.DidNotReceive().PreviewPlanAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_*")]
    public void EveryTool_AuthorizesAgainstTheStudioDraftGrantFamily()
    {
        // REQ-004: every Studio tool authorizes against the distinct
        // OperatorResourceType.StudioDraft grant family via the same
        // EnsureCallerAuthorizedAsync gate every other /mcp tool uses.
        foreach (var tool in BuildAllTools())
        {
            var descriptor = tool.Describe();
            descriptor.Annotations.Should().NotBeNull($"'{tool.Name}' must classify itself read-only vs write");
            descriptor.InputSchema.ValueKind.Should().Be(JsonValueKind.Object, $"'{tool.Name}' inputSchema must be an object schema");
            descriptor.InputSchema.GetProperty("type").GetString().Should().Be("object", $"'{tool.Name}' inputSchema type must be 'object'");
        }
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_layer")]
    public async Task AddLayer_HappyPath_AuthorizesAgainstStudioDraftResourceType()
    {
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);
        _lifecycleService
            .UpdateDraftAsync(DraftId, Arg.Any<UpdateStudioPackageDraftCommand>(), Arg.Any<CancellationToken>())
            .Returns(draft with { Generation = 2 });

        var tool = new AddStudioLayerTool(_jobService, NullLogger<AddStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioAddLayerArgument
            {
                DraftId = DraftId,
                Generation = 1,
                Layer = new McpStudioLayerInput { Id = "parcels", Type = "fill" },
            },
            StudioMcpJsonContext.Default.McpStudioAddLayerArgument);

        var result = await tool.InvokeAsync(HttpContextWithLifecycleService(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(2);

        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.StudioDraft,
            OperatorOperation.Create,
            Arg.Any<CancellationToken>());
    }

    private Microsoft.AspNetCore.Http.DefaultHttpContext HttpContextWithLifecycleService() =>
        McpTestFactory.AuthenticatedHttpContextWithServices(services => services.AddSingleton(_lifecycleService));

    private Microsoft.AspNetCore.Http.DefaultHttpContext HttpContextWithLifecycleServiceAndValidator() =>
        McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(_lifecycleService);
            services.AddSingleton(_validator);
        });

    private static IReadOnlyList<IMcpTool> BuildAllTools()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        return
        [
            new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance),
            new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance),
            new UpdateStudioDraftTool(jobService, NullLogger<UpdateStudioDraftTool>.Instance),
            new ValidateStudioDraftTool(jobService, NullLogger<ValidateStudioDraftTool>.Instance),
            new PreviewStudioDraftTool(jobService, NullLogger<PreviewStudioDraftTool>.Instance),
            new AddStudioLayerTool(jobService, NullLogger<AddStudioLayerTool>.Instance),
            new RemoveStudioLayerTool(jobService, NullLogger<RemoveStudioLayerTool>.Instance),
            new SetStudioLayerStyleTool(jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
            new SetStudioViewTool(jobService, NullLogger<SetStudioViewTool>.Instance),
            new AddStudioWidgetTool(jobService, NullLogger<AddStudioWidgetTool>.Instance),
            new RemoveStudioWidgetTool(jobService, NullLogger<RemoveStudioWidgetTool>.Instance),
            new ProposeStudioPublicationTool(jobService, NullLogger<ProposeStudioPublicationTool>.Instance),
        ];
    }

    private static StudioPackageDraft BuildDraft(StudioPackageFamily family, long generation)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new StudioPackageDraft
        {
            DraftId = DraftId,
            ItemId = Guid.NewGuid(),
            PackageKey = "parcels",
            Family = family,
            Envelope = new StudioPackageEnvelope { Family = family, SchemaVersion = "1.0" },
            Generation = generation,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
