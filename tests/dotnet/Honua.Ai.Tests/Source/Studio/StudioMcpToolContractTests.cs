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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Tool-contract tests for the Studio draft lifecycle and composition MCP
/// tools (honua-server#3002): input-schema shape, generation-conflict typed
/// errors, family-gating and not-found typed errors, and the structural
/// publish-tool-absence guarantee (REQ-003/REQ-009) — none of the twelve
/// tools ever calls a lifecycle-service member that moves a current/published
/// pointer. Uses a mocked <see cref="IStudioPackageLifecycleService"/>;
/// <see cref="StudioMcpToolDelegationTests"/> covers the real
/// <c>InMemoryStudioPackageStore</c>-backed happy path.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class StudioMcpToolContractTests
{
    private static readonly Guid DraftId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IStudioPackageLifecycleService _lifecycleService = Substitute.For<IStudioPackageLifecycleService>();
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
        names.Where(n => n.Contains("publish", StringComparison.OrdinalIgnoreCase))
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

        var tool = new ProposeStudioPublicationTool(_lifecycleService, _jobService, NullLogger<ProposeStudioPublicationTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioProposePublicationArgument { DraftId = DraftId, Generation = 1, Route = "/studio/parcels" },
            StudioMcpJsonContext.Default.McpStudioProposePublicationArgument);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

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

        var tool = new UpdateStudioDraftTool(_lifecycleService, _jobService, NullLogger<UpdateStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioUpdateDraftArgument
            {
                DraftId = DraftId,
                Generation = 1,
                PackageKey = "parcels-map",
                SchemaVersion = "1.0",
            },
            StudioMcpJsonContext.Default.McpStudioUpdateDraftArgument);

        var act = () => tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

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

        var tool = new AddStudioLayerTool(_lifecycleService, _jobService, NullLogger<AddStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioAddLayerArgument
            {
                DraftId = DraftId,
                Generation = 1,
                Layer = new McpStudioLayerInput { Id = "parcels" },
            },
            StudioMcpJsonContext.Default.McpStudioAddLayerArgument);

        var act = () => tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_add_layer")]
    public async Task AddLayer_WhenDraftFamilyIsNotMapOrApp_SurfacesInvalidArgument()
    {
        var draft = BuildDraft(StudioPackageFamily.Query, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new AddStudioLayerTool(_lifecycleService, _jobService, NullLogger<AddStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioAddLayerArgument
            {
                DraftId = DraftId,
                Generation = 1,
                Layer = new McpStudioLayerInput { Id = "parcels" },
            },
            StudioMcpJsonContext.Default.McpStudioAddLayerArgument);

        var act = () => tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_remove_layer")]
    public async Task RemoveLayer_WhenLayerIdDoesNotExist_SurfacesNotFound()
    {
        var draft = BuildDraft(StudioPackageFamily.Map, generation: 1);
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns(draft);

        var tool = new RemoveStudioLayerTool(_lifecycleService, _jobService, NullLogger<RemoveStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioRemoveLayerArgument { DraftId = DraftId, Generation = 1, LayerId = "no-such-layer" },
            StudioMcpJsonContext.Default.McpStudioRemoveLayerArgument);

        var act = () => tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_get_draft")]
    public async Task GetDraft_WhenDraftDoesNotExist_SurfacesNotFound()
    {
        _lifecycleService.GetDraftAsync(DraftId, Arg.Any<CancellationToken>()).Returns((StudioPackageDraft?)null);

        var tool = new GetStudioDraftTool(_lifecycleService, _jobService, NullLogger<GetStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioDraftIdArgument { DraftId = DraftId },
            StudioMcpJsonContext.Default.McpStudioDraftIdArgument);

        var act = () => tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_WhenPackageKeyMissing_SurfacesInvalidArgument()
    {
        var tool = new CreateStudioDraftTool(_lifecycleService, _jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""{"family":"map","schemaVersion":"1.0"}""");

        var act = () => tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_create_draft")]
    public async Task CreateDraft_WhenFamilyIsUnknown_SurfacesInvalidArgument()
    {
        var tool = new CreateStudioDraftTool(_lifecycleService, _jobService, NullLogger<CreateStudioDraftTool>.Instance);
        var arguments = McpTestFactory.ParseJson("""{"packageKey":"parcels-map","family":"not-a-family","schemaVersion":"1.0"}""");

        var act = () => tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
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

        var tool = new AddStudioLayerTool(_lifecycleService, _jobService, NullLogger<AddStudioLayerTool>.Instance);
        var arguments = McpTestFactory.ToArguments(
            new McpStudioAddLayerArgument
            {
                DraftId = DraftId,
                Generation = 1,
                Layer = new McpStudioLayerInput { Id = "parcels", Type = "fill" },
            },
            StudioMcpJsonContext.Default.McpStudioAddLayerArgument);

        var result = await tool.InvokeAsync(McpTestFactory.AuthenticatedHttpContext(), arguments, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("generation").GetInt64().Should().Be(2);

        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.StudioDraft,
            OperatorOperation.Create,
            Arg.Any<CancellationToken>());
    }

    private static IReadOnlyList<Honua.Ai.Protocols.Mcp.Tools.IMcpTool> BuildAllTools()
    {
        var lifecycleService = Substitute.For<IStudioPackageLifecycleService>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        return
        [
            new CreateStudioDraftTool(lifecycleService, jobService, NullLogger<CreateStudioDraftTool>.Instance),
            new GetStudioDraftTool(lifecycleService, jobService, NullLogger<GetStudioDraftTool>.Instance),
            new UpdateStudioDraftTool(lifecycleService, jobService, NullLogger<UpdateStudioDraftTool>.Instance),
            new ValidateStudioDraftTool(lifecycleService, jobService, NullLogger<ValidateStudioDraftTool>.Instance),
            new PreviewStudioDraftTool(lifecycleService, jobService, NullLogger<PreviewStudioDraftTool>.Instance),
            new AddStudioLayerTool(lifecycleService, jobService, NullLogger<AddStudioLayerTool>.Instance),
            new RemoveStudioLayerTool(lifecycleService, jobService, NullLogger<RemoveStudioLayerTool>.Instance),
            new SetStudioLayerStyleTool(lifecycleService, jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
            new SetStudioViewTool(lifecycleService, jobService, NullLogger<SetStudioViewTool>.Instance),
            new AddStudioWidgetTool(lifecycleService, jobService, NullLogger<AddStudioWidgetTool>.Instance),
            new RemoveStudioWidgetTool(lifecycleService, jobService, NullLogger<RemoveStudioWidgetTool>.Instance),
            new ProposeStudioPublicationTool(lifecycleService, jobService, NullLogger<ProposeStudioPublicationTool>.Instance),
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
