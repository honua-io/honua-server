// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class StudioVersionMcpTests
{
    private static readonly string MapBody = """
        {"mapPackageId":"fixture-map","format":"honua_map_package.v1","status":"Ready","createdAt":"1970-01-01T00:00:00Z","layers":[{"id":"parcels","type":"fill","sourceId":"content.parcels","styleRef":"style_parcels"}],
         "view":{"center":[-157.86,21.31],"zoom":10},"widgets":[{"id":"legend","kind":"legend"}]}
        """;

    [UnitTest]
    public Task SaveAndReopen_UseRealDurableRuntime_AndPreserveIndependentMapValues()
        => AssertSaveAndReopenAsync(StudioPackageFamily.Map);

    [UnitTest]
    public Task SaveAndReopen_PreserveIndependentDashboardValues()
        => AssertSaveAndReopenAsync(StudioPackageFamily.Dashboard);

    private static async Task AssertSaveAndReopenAsync(StudioPackageFamily family)
    {
        using var provider = LifecycleProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await SeedAsync(lifecycle, family);
        var instances = new VolatileOperationInstanceStore();
        var runtime = Runtime(lifecycle, instances, PolicyDecisionKind.Allow);
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton<IStudioDraftMutationRuntime>(runtime);
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var save = new SaveStudioVersionTool(jobs, NullLogger<SaveStudioVersionTool>.Instance);
        using var activity = new Activity("studio-save-audit").Start();
        var saved = await save.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"draftId":"{{draft.DraftId}}","generation":1,"changeNote":"known map fixture"}"""), default);
        saved.IsError.Should().BeFalse();
        activity.GetTagItem("studio.generation.before").Should().Be(1L);
        activity.GetTagItem("studio.generation.after").Should().Be(2L);
        var savedBody = saved.StructuredContent!.Value;
        var versionId = savedBody.GetProperty("version").GetProperty("versionId").GetGuid();
        var savedEnvelope = await instances.GetAsync(savedBody.GetProperty("operation").GetProperty("operationInstanceId").GetString()!);
        savedEnvelope!.Status.Should().Be(OperationHandleStatus.Completed);
        savedEnvelope.AuditId.Should().NotBeNullOrWhiteSpace();
        var version = (await lifecycle.GetVersionAsync(draft.ItemId, versionId))!;
        var expectedEnvelope = draft.Envelope with
        {
            Validation = new StudioValidationSummary { Status = StudioPackageValidationStatus.Valid },
        };
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(expectedEnvelope, StudioJsonContext.Default.StudioPackageEnvelope)));
        savedEnvelope.ResourceIds.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["itemId"] = draft.ItemId.ToString("D"), ["versionId"] = versionId.ToString("D"), ["contentHash"] = expectedHash,
        });
        await AssertPollableResultAsync(savedEnvelope, instances);
        version.ContentHash.Should().Be(expectedHash, "the immutable hash binds the seeded envelope, not the subsequently edited draft");
        version.Validation.Status.Should().Be(StudioPackageValidationStatus.Valid);
        version.Validation.Diagnostics.Should().BeEmpty();
        version.VersionNumber.Should().Be(1);
        version.PackageKey.Should().Be("terminal-parcels");
        version.OwnerId.Should().Be("test-user");
        AssertMap(version.Envelope, family);

        (await lifecycle.GetDraftAsync(draft.DraftId))!.Generation.Should().Be(2,
            "saving persists refreshed validation and advances the mutable draft generation");
        // Mutate the original draft after saving: reopen must load the immutable version.
        await lifecycle.UpdateDraftAsync(draft.DraftId, new UpdateStudioPackageDraftCommand
        {
            PackageKey = draft.PackageKey,
            OwnerId = draft.OwnerId,
            Generation = 2,
            Envelope = draft.Envelope with { Body = McpTestFactory.ParseJson("""{"layers":[],"view":{"center":[0,0],"zoom":1}}""") },
            ActorId = "test-user",
        });
        var reopen = new ReopenStudioVersionTool(jobs, NullLogger<ReopenStudioVersionTool>.Instance);
        var reopened = await reopen.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"itemId":"{{draft.ItemId}}","versionId":"{{versionId}}"}"""), default);
        reopened.IsError.Should().BeFalse();
        var reopenedBody = reopened.StructuredContent!.Value;
        var reopenedDraft = (await lifecycle.GetDraftAsync(reopenedBody.GetProperty("draftId").GetGuid()))!;
        reopenedDraft.DraftId.Should().NotBe(draft.DraftId);
        reopenedDraft.BaseVersionId.Should().Be(versionId);
        reopenedDraft.ItemId.Should().Be(draft.ItemId);
        reopenedDraft.Generation.Should().Be(1);
        AssertMap(reopenedDraft.Envelope, family);
        var reopenedEnvelope = await instances.GetAsync(reopenedBody.GetProperty("operation").GetProperty("operationInstanceId").GetString()!);
        reopenedEnvelope!.Status.Should().Be(OperationHandleStatus.Completed);
        reopenedEnvelope.AuditId.Should().NotBeNullOrWhiteSpace();
        reopenedEnvelope.ResourceIds.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["draftId"] = reopenedDraft.DraftId.ToString("D"), ["generation"] = "1",
        });
        await AssertPollableResultAsync(reopenedEnvelope, instances);
        (await lifecycle.GetPointersAsync(draft.ItemId))!.CurrentVersionId.Should().Be(versionId);
    }

    [UnitTest]
    public async Task Save_RequiresApproval_ReturnsProposalWithoutSavingVersion()
    {
        using var provider = LifecycleProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await SeedAsync(lifecycle);
        var instances = new VolatileOperationInstanceStore();
        var approval = Substitute.For<IOperationApprovalBridge>();
        approval.CreateProposalAsync(Arg.Any<IOperationDescriptor>(), Arg.Any<OperationRequest>(),
            Arg.Any<OperationPolicyContext>(), Arg.Any<PolicyDecision>(), Arg.Any<CancellationToken>())
            .Returns(new OperationApprovalBridgeResult { IsDurable = true, ProposalId = "proposal-save", AuditId = "audit-proposal" });
        var runtime = Runtime(lifecycle, instances, PolicyDecisionKind.RequireApproval, approval);
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton<IStudioDraftMutationRuntime>(runtime);
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });
        var tool = new SaveStudioVersionTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<SaveStudioVersionTool>.Instance);
        var result = await tool.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"draftId":"{{draft.DraftId}}","generation":1}"""), default);
        var resultBody = Assert.IsType<JsonElement>(result.StructuredContent);
        var operationId = resultBody.GetProperty("operation").GetProperty("operationInstanceId").GetString()!;
        var operation = (await instances.GetAsync(operationId))!;
        operation.Status.Should().Be(OperationHandleStatus.RequiresApproval);
        operation.ProposalId.Should().Be("proposal-save");
        (await lifecycle.GetPointersAsync(draft.ItemId))!.CurrentVersionId.Should().BeNull();
        resultBody.TryGetProperty("version", out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task Save_ParentOwnerDenied_RefusesBeforeMutation()
    {
        using var provider = LifecycleProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await SeedAsync(lifecycle);
        var authorization = Substitute.For<IStudioAuthorizationService>();
        authorization.ResolveCallerId(Arg.Any<System.Security.Claims.ClaimsPrincipal>()).Returns("test-user");
        authorization.AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<string?>(),
            StudioAuthorizationOperation.CreateVersion, Arg.Any<string?>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string?>(5) == draft.DraftId.ToString("D")
                ? StudioAuthorizationDecision.Allow()
                : StudioAuthorizationDecision.Deny("studio_authorization/owner_required", "Parent item is owned by another caller."));
        var runtime = Substitute.For<IStudioDraftMutationRuntime>();
        // A loaded resource authorization failure must not be hidden by the generic grant.
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(authorization);
            services.AddSingleton(runtime);
        });
        var tool = new SaveStudioVersionTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<SaveStudioVersionTool>.Instance);
        // Draft permission alone cannot authorize advancement of the parent item pointer.
        var act = () => tool.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"draftId":"{{draft.DraftId}}","generation":1}"""), default);
        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>()
            .WithMessage("Parent item is owned by another caller.");
        await runtime.DidNotReceiveWithAnyArgs().SaveVersionAsync(default, default, default, default, default!, default);
    }

    [UnitTest]
    public async Task Reopen_OwnerDenied_RefusesBeforeMutation()
    {
        using var provider = LifecycleProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await SeedAsync(lifecycle);
        var version = (await lifecycle.SaveDraftAsVersionAsync(draft.DraftId, "seed", "test-user", 1))!;
        var authorization = Substitute.For<IStudioAuthorizationService>();
        authorization.ResolveCallerId(Arg.Any<System.Security.Claims.ClaimsPrincipal>()).Returns("other-user");
        authorization.AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<string?>(),
            StudioAuthorizationOperation.ReopenVersion, Arg.Any<string?>(), false, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(StudioAuthorizationDecision.Deny("studio_authorization/owner_required", "Saved version belongs to another caller."));
        var runtime = Substitute.For<IStudioDraftMutationRuntime>();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            services.AddSingleton(authorization);
            services.AddSingleton(runtime);
        }, user: "other-user");
        var tool = new ReopenStudioVersionTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<ReopenStudioVersionTool>.Instance);
        var act = () => tool.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"itemId":"{{draft.ItemId}}","versionId":"{{version.VersionId}}"}"""), default);
        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>().WithMessage("Saved version belongs to another caller.");
        await runtime.DidNotReceiveWithAnyArgs().ReopenVersionAsync(default, default, default, default!, default);
    }

    [UnitTest]
    public async Task Save_StaleGeneration_RefusesBeforeSaving()
    {
        using var provider = LifecycleProvider();
        var lifecycle = provider.GetRequiredService<IStudioPackageLifecycleService>();
        var draft = await SeedAsync(lifecycle);
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(lifecycle);
            McpTestFactory.AddAllowingStudioAuthorization(services);
        });
        var tool = new SaveStudioVersionTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<SaveStudioVersionTool>.Instance);
        var act = () => tool.InvokeAsync(context,
            McpTestFactory.ParseJson($$"""{"draftId":"{{draft.DraftId}}","generation":2}"""), default);
        await act.Should().ThrowAsync<StudioDraftGenerationConflictException>();
        (await lifecycle.GetPointersAsync(draft.ItemId))!.CurrentVersionId.Should().BeNull();
    }

    private static async Task AssertPollableResultAsync(OperationHandle operation, IOperationInstanceStore instances)
    {
        var proposal = new OperationProposal
        {
            ProposalId = "proposal-result", OperationId = operation.OperationId, RequestedBy = "test-user",
            Kind = OperationClass.StudioDraftMutation, Status = OperationProposalStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new() { OperationInstanceId = operation.OperationInstanceId },
        };
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>()).Returns(_ => proposal);
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(instances);
            services.AddSingleton(Substitute.For<IGeoprocessingJobService>());
        });
        var resource = new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance);
        async Task<JsonElement> ReadAsync()
        {
            var response = await resource.ReadAsync(context, "honua://proposals/proposal-result", default);
            return McpTestFactory.ParseJson(response.Contents.Single().Text!);
        }
        var result = await ReadAsync();
        result.GetProperty("resourceIds").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
            .Should().BeEquivalentTo(operation.ResourceIds);
        proposal = proposal with { Status = OperationProposalStatus.AwaitingApproval };
        (await ReadAsync()).TryGetProperty("resourceIds", out _).Should().BeFalse();
        proposal = proposal with { Status = OperationProposalStatus.Succeeded, RequestedBy = "another-user" };
        (await ReadAsync()).TryGetProperty("resourceIds", out _).Should().BeFalse();
        proposal = proposal with { RequestedBy = "test-user", TenantId = "another-tenant" };
        var crossTenant = () => ReadAsync();
        await crossTenant.Should().ThrowAsync<KeyNotFoundException>();
    }

    private static StudioDraftMutationRuntime Runtime(IStudioPackageLifecycleService lifecycle,
        IOperationInstanceStore instances, PolicyDecisionKind decision, IOperationApprovalBridge? approval = null)
    {
        var catalog = new OperationCatalog([new ServerOperationDescriptorProvider()], TimeProvider.System);
        var policy = new ConfigurableOperationPolicyDecisionPoint(Options.Create(new OperationPolicyOptions
        {
            Enabled = true,
            DefaultDecision = decision,
            DefaultApprovalLane = "studio-test",
        }));
        var invoker = new OperationDispatcher(catalog,
            [new StudioSaveVersionExecutor(lifecycle, TimeProvider.System), new StudioReopenVersionExecutor(lifecycle, TimeProvider.System)],
            policy, TimeProvider.System, approvalBridge: approval, instanceStore: instances);
        return new StudioDraftMutationRuntime(invoker, instances);
    }

    private static ServiceProvider LifecycleProvider()
    {
        var services = new ServiceCollection();
        services.AddStudioPackageLifecycle();
        return services.BuildServiceProvider();
    }

    private static Task<StudioPackageDraft> SeedAsync(IStudioPackageLifecycleService lifecycle, StudioPackageFamily family = StudioPackageFamily.Map) => lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
    {
        PackageKey = "terminal-parcels",
        OwnerId = "test-user",
        ActorId = "test-user",
        Envelope = new StudioPackageEnvelope
        {
            Family = family,
            SchemaVersion = "1.0",
            Format = family == StudioPackageFamily.Map ? "honua_map_package.v1" : "studio_dashboard_package.v1",
            Body = McpTestFactory.ParseJson(family == StudioPackageFamily.Map ? MapBody :
                MapBody.Replace("honua_map_package.v1", "studio_dashboard_package.v1", StringComparison.Ordinal)),
        },
    });

    private static void AssertMap(StudioPackageEnvelope envelope, StudioPackageFamily family)
    {
        envelope.Family.Should().Be(family);
        envelope.Format.Should().Be(family == StudioPackageFamily.Map ? "honua_map_package.v1" : "studio_dashboard_package.v1");
        envelope.SchemaVersion.Should().Be("1.0");
        var body = StudioCompositionBodyEditor.ReadBody(envelope);
        body.Layers.Should().ContainSingle(layer => layer.Id == "parcels" && layer.SourceId == "content.parcels" && layer.StyleRef == "style_parcels");
        body.View!.Center!.Should().Equal(-157.86, 21.31);
        body.View.Zoom.Should().Be(10);
        body.Widgets.Should().ContainSingle(widget => widget.Id == "legend" && widget.Kind == "legend");
    }
}
