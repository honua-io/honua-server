// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Geoprocessing.Tests;

public sealed partial class ServerJobArtifactImporterTests
{
    [UnitTest]
    public async Task StagePreviewOperation_ReturnsBindingAndServerOwnedTable_WithoutImport()
    {
        var harness = await OperationHarnessAsync();

        var handle = await harness.Preview.SubmitAsync(harness.Request(StagedImportOperation.PreviewOperationId),
            harness.Context(), CancellationToken.None);

        var details = handle.Result!.Details;
        details[StagedImportOperation.PreviewFingerprintParameter].Should().HaveLength(64);
        details["stagingTable"].Should().Be(StagedImportOperation.DestinationTable("tenant-a", "actor-1", "intent-buffer"));
        details["stagingTable"].Should().StartWith("staged_");
        details["featureCount"].Should().Be("1");
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_CallerNamedTable_IsRefused()
    {
        var harness = await OperationHarnessAsync();
        var request = harness.Request(StagedImportOperation.PreviewOperationId, ("table", "existing_parcels"));

        var act = () => harness.Preview.SubmitAsync(request, harness.Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task StageImport_ProposeThenApprovedReplay_ImportsCreateOnlyOnce()
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        IOperationApprovalRequestMapper mapper = new StagedImportApprovalMapper();
        var gateway = mapper.Map(StagedImportOperation.BuildDescriptors()[1], prepared, harness.Context(),
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval, Reason = "operator approval" });
        harness.Recording.Request.Should().BeNull("a proposal must not import");

        var replay = mapper.MapReplay(gateway).Request;
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };
        replay = await harness.Import.PrepareAsync(replay, approved, CancellationToken.None);
        (await harness.Import.ValidateAsync(replay, CancellationToken.None)).IsValid.Should().BeTrue();
        var first = await harness.Import.SubmitAsync(replay, approved, CancellationToken.None);
        var duplicate = await harness.Import.SubmitAsync(replay, approved, CancellationToken.None);

        harness.Recording.Request!.LoadMode.Should().Be(ImportLoadMode.CreateOnly);
        harness.Recording.Request.OverwriteExisting.Should().BeFalse();
        harness.Recording.Request.TableName.Should().Be(StagedImportOperation.DestinationTable("tenant-a", "actor-1", "intent-buffer"));
        duplicate.Result!.Details["stagingTable"].Should().Be(first.Result!.Details["stagingTable"]);
        await harness.Recording.Service.Received(1).ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task StageImport_SchemaRemovedAfterPreview_RefusesProposalBeforeReadingArtifact()
    {
        var harness = await OperationHarnessAsync();
        var preview = await harness.Preview.SubmitAsync(
            harness.Request(StagedImportOperation.PreviewOperationId), harness.Context(), CancellationToken.None);
        var fingerprint = preview.Result!.Details[StagedImportOperation.PreviewFingerprintParameter];
        var configuration = harness.Services.GetRequiredService<IConfiguration>();
        // The preview destination is absent from the current operational-schema configuration.
        configuration[ImportTargetSchemaPolicy.DefaultOperationalSchemaConfigurationKey] = "replacement_data";
        harness.Jobs.ClearReceivedCalls();
        var request = harness.Request(StagedImportOperation.ImportOperationId,
            (StagedImportOperation.PreviewFingerprintParameter, fingerprint));

        var act = () => harness.Import.PrepareAsync(request, harness.Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>()
            .WithMessage(ImportTargetSchemaPolicy.NotOperationalSchemaMessage);
        await harness.Jobs.DidNotReceive().GetJobAsync(harness.Reference.JobId,
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_SourceChangedBetweenPreviewAndProposal_IsRefused()
    {
        var harness = await OperationHarnessAsync();
        var request = harness.Request(StagedImportOperation.ImportOperationId,
            (StagedImportOperation.PreviewFingerprintParameter, new string('0', 64)));

        var act = () => harness.Import.PrepareAsync(request, harness.Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    public async Task StageImport_CallerSuppliedPin_IsRefused()
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        var forged = harness.Request(StagedImportOperation.ImportOperationId,
            prepared.Parameters.Select(pair => (pair.Key, pair.Value!)).ToArray());

        var act = () => harness.Import.PrepareAsync(forged, harness.Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task StageImport_ApprovalWindowPassed_IsInvalidAndDoesNotImport()
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        harness.Time.Advance(StagedImportOperation.ApprovalWindow + TimeSpan.FromSeconds(1));
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };

        (await harness.Import.ValidateAsync(prepared, CancellationToken.None)).IsValid.Should().BeFalse();
        var act = () => harness.Import.SubmitAsync(prepared, approved, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTheory]
    [InlineData("tenant")]
    [InlineData("requester")]
    public async Task StageImport_ReplayUnderAnotherScope_IsRefused(string change)
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };
        approved = change == "tenant" ? approved with { TenantId = "tenant-b" } : approved with { PrincipalId = "actor-2" };

        var act = () => harness.Import.SubmitAsync(prepared, approved, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_ArtifactReplacedAfterApproval_IsRefusedBeforeImport()
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        var changed = await BufferArtifactAsync("3857", "25");
        harness.Jobs.GetJobAsync(harness.Reference.JobId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedJob(changed.Uri, 2));
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };

        var act = () => harness.Import.SubmitAsync(prepared, approved, CancellationToken.None);

        // A replaced artifact differs from the approved server reference.
        (await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>())
            .WithMessage("*artifact reference does not match*");
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_WithoutAuthenticatedRequest_FailsClosed()
    {
        var harness = await OperationHarnessAsync(new ClaimsPrincipal(new ClaimsIdentity()));
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };
        var replay = harness.Request(StagedImportOperation.ImportOperationId,
            [.. new StagedImportPin(new ServerJobArtifactStagePreview("c", new string('a', 64))
            {
                FeatureCount = 1, SourceSrid = 3857, CrsState = "known",
            }, harness.Time.GetUtcNow().AddMinutes(5)).Parameters().Select(pair => (pair.Key, pair.Value!))]);

        var act = () => harness.Import.SubmitAsync(replay, approved, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_ApprovedReplay_ReadsArtifactAsTheApprover()
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        var approver = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "approver-9")], "Test"));
        harness.Http.User = approver;
        harness.Jobs.ClearReceivedCalls();

        await harness.Import.SubmitAsync(prepared, harness.Context() with { ApprovedProposalId = "proposal-1" },
            CancellationToken.None);

        await harness.Jobs.Received().GetJobAsync(harness.Reference.JobId,
            Arg.Is<ClaimsPrincipal>(principal => ReferenceEquals(principal, approver)), Arg.Any<CancellationToken>());
        await harness.Jobs.DidNotReceive().GetJobAsync(harness.Reference.JobId,
            Arg.Is<ClaimsPrincipal>(principal => !ReferenceEquals(principal, approver)), Arg.Any<CancellationToken>());
        harness.Recording.Request!.LoadMode.Should().Be(ImportLoadMode.CreateOnly);
    }

    [UnitTheory]
    [InlineData("preview")]
    [InlineData("propose")]
    [InlineData("replay")]
    public async Task StageImport_CallerWithoutImportAuthority_IsRefused(string step)
    {
        var harness = await OperationHarnessAsync();
        var prepared = step == "replay" ? await harness.PrepareProposalAsync() : null;
        harness.Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        Func<Task> act = step switch
        {
            "preview" => () => harness.Preview.SubmitAsync(harness.Request(StagedImportOperation.PreviewOperationId),
                harness.Context(), CancellationToken.None),
            "propose" => () => harness.Import.PrepareAsync(harness.Request(StagedImportOperation.ImportOperationId,
                (StagedImportOperation.PreviewFingerprintParameter, new string('a', 64))), harness.Context(), CancellationToken.None),
            _ => () => harness.Import.SubmitAsync(prepared!, harness.Context() with { ApprovedProposalId = "proposal-1" },
                CancellationToken.None),
        };

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        harness.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task StageImport_ValidateWithoutBinding_ReportsInvalid()
    {
        var harness = await OperationHarnessAsync();

        var validation = await harness.Import.ValidateAsync(harness.Request(StagedImportOperation.ImportOperationId),
            CancellationToken.None);

        validation.IsValid.Should().BeFalse();
        validation.Messages.Should().ContainSingle().Which.Should().Contain("previewFingerprint");
    }

    [UnitTest]
    public void StageImportDescriptors_GateImportAndKeepPreviewReadOnly()
    {
        var descriptors = StagedImportOperation.BuildDescriptors();

        descriptors.Single(d => d.OperationId == StagedImportOperation.PreviewOperationId).Policy.SideEffectClass
            .Should().Be(OperationSideEffectClass.ReadOnly);
        var import = descriptors.Single(d => d.OperationId == StagedImportOperation.ImportOperationId);
        import.ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        import.InputSchema.Should().NotContain(parameter => parameter.Name == "table");
        var discard = descriptors.Single(d => d.OperationId == StagedImportOperation.DiscardOperationId);
        discard.ApprovalModel.Should().Be(OperationApprovalModel.OperatorGate);
        discard.Policy.SideEffectClass.Should().Be(OperationSideEffectClass.DestroysState);
        discard.InputSchema.Select(p => p.Name).Should().Equal("intentId");
    }

    [UnitTest]
    public async Task StageDiscard_ProposeReplayRepeatedAndRetryAfterDiscard_BehavesAsExpected()
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };
        await harness.Import.SubmitAsync(prepared, approved, CancellationToken.None);

        IOperationApprovalRequestMapper mapper = new StagedImportApprovalMapper(StagedImportOperation.DiscardOperationId);
        var discardRequest = new OperationRequest
        {
            OperationId = StagedImportOperation.DiscardOperationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["intentId"] = "intent-buffer" },
        };
        var discardDescriptor = StagedImportOperation.BuildDescriptors()
            .Single(d => d.OperationId == StagedImportOperation.DiscardOperationId);
        var gateway = mapper.Map(discardDescriptor, discardRequest, harness.Context(),
            new PolicyDecision { Kind = PolicyDecisionKind.RequireApproval, Reason = "operator approval" });
        var replay = mapper.MapReplay(gateway).Request;

        var first = await harness.Discard.SubmitAsync(replay, approved, CancellationToken.None);
        first.Result!.Details["alreadyDiscarded"].Should().Be("false");
        first.Result.Details["stagingTable"].Should().Be(StagedImportOperation.DestinationTable("tenant-a", "actor-1", "intent-buffer"));

        var second = await harness.Discard.SubmitAsync(replay, approved, CancellationToken.None);
        second.Result!.Details["alreadyDiscarded"].Should().Be("true");
        second.Result.Details["physicalTable"].Should().Be(first.Result.Details["physicalTable"]);

        var retryImport = () => harness.Import.SubmitAsync(prepared, approved, CancellationToken.None);
        await retryImport.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*already discarded*");
    }

    [UnitTheory]
    [InlineData("tenant")]
    [InlineData("requester")]
    public async Task StageDiscard_WrongTenantOrRequester_IsRefused(string change)
    {
        var harness = await OperationHarnessAsync();
        var prepared = await harness.PrepareProposalAsync();
        await harness.Import.SubmitAsync(prepared, harness.Context() with { ApprovedProposalId = "proposal-1" },
            CancellationToken.None);

        var otherScope = harness.Context() with { ApprovedProposalId = "proposal-2" };
        otherScope = change == "tenant" ? otherScope with { TenantId = "tenant-b" } : otherScope with { PrincipalId = "actor-2" };
        var discardRequest = new OperationRequest
        {
            OperationId = StagedImportOperation.DiscardOperationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["intentId"] = "intent-buffer" },
        };

        var act = () => harness.Discard.SubmitAsync(discardRequest, otherScope, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
    }

    [UnitTest]
    public async Task StageDiscard_ReferencedTable_IsRefused()
    {
        var ledger = new MemoryServerJobArtifactImportLedger(isReferenced: static (_, _) => true);
        var harness = await OperationHarnessAsync(Principal(), ledger);
        var prepared = await harness.PrepareProposalAsync();
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };
        await harness.Import.SubmitAsync(prepared, approved, CancellationToken.None);

        var discardRequest = new OperationRequest
        {
            OperationId = StagedImportOperation.DiscardOperationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["intentId"] = "intent-buffer" },
        };

        var act = () => harness.Discard.SubmitAsync(discardRequest, approved, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*referenced*");
    }

    [UnitTest]
    public async Task StageDiscard_NotOwnedTable_IsRefused()
    {
        var ledger = new MemoryServerJobArtifactImportLedger(isOwned: static (_, _) => false);
        var harness = await OperationHarnessAsync(Principal(), ledger);
        var prepared = await harness.PrepareProposalAsync();
        var approved = harness.Context() with { ApprovedProposalId = "proposal-1" };
        await harness.Import.SubmitAsync(prepared, approved, CancellationToken.None);

        var discardRequest = new OperationRequest
        {
            OperationId = StagedImportOperation.DiscardOperationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["intentId"] = "intent-buffer" },
        };

        var act = () => harness.Discard.SubmitAsync(discardRequest, approved, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>()
            .WithMessage("*not an import-owned*");
    }

    [UnitTheory]
    [InlineData("table", "existing_parcels")]
    [InlineData("schema", "public")]
    public async Task StageDiscard_CallerSuppliedTableOrSchema_IsRefused(string key, string value)
    {
        var harness = await OperationHarnessAsync();
        var request = new OperationRequest
        {
            OperationId = StagedImportOperation.DiscardOperationId,
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["intentId"] = "intent-buffer",
                [key] = value,
            },
        };

        (await harness.Discard.ValidateAsync(request, CancellationToken.None)).IsValid.Should().BeFalse();
        var act = () => harness.Discard.SubmitAsync(request, harness.Context(), CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    private static Task<OperationHarness> OperationHarnessAsync() => OperationHarnessAsync(Principal(), null);

    private static Task<OperationHarness> OperationHarnessAsync(ClaimsPrincipal user) => OperationHarnessAsync(user, null);

    private static async Task<OperationHarness> OperationHarnessAsync(ClaimsPrincipal user, IServerJobArtifactImportLedger? ledger)
    {
        var artifact = await BufferArtifactAsync("3857", "10");
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var job = CompletedJob(artifact.Uri, 2);
        jobs.GetJobAsync(job.OperationId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(job);
        var recording = new RecordingImports();
        var time = new SteppedTimeProvider(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
        // A stub, not HttpContextAccessor: its async-local context would not flow out of this helper.
        var http = new DefaultHttpContext { User = user };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build())
            .AddSingleton(jobs)
            .AddSingleton(recording.Service)
            .AddSingleton<IServerJobArtifactImportLedger>(ledger ?? new MemoryServerJobArtifactImportLedger())
            .AddSingleton<IOperationInstanceStore, Honua.Core.Features.Operations.Services.VolatileOperationInstanceStore>()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton(accessor)
            .AddSingleton(authorization)
            .BuildServiceProvider();
        http.RequestServices = services;
        return new OperationHarness(services)
        {
            Jobs = jobs,
            Recording = recording,
            Time = time,
            Reference = Reference(artifact, 2, 1),
            Http = http,
            Authorization = authorization,
        };
    }

    private sealed record OperationHarness(IServiceProvider Services)
    {
        internal required IGeoprocessingJobService Jobs { get; init; }

        internal required RecordingImports Recording { get; init; }

        internal required SteppedTimeProvider Time { get; init; }

        internal required DatasetImportServerJobArtifactReference Reference { get; init; }

        internal required DefaultHttpContext Http { get; init; }

        internal required IAuthorizationService Authorization { get; init; }

        internal StagedImportPreviewExecutor Preview { get; } = new(Services);

        internal StagedImportExecutor Import { get; } = new(Services);

        internal StagedImportDiscardExecutor Discard { get; } = new(Services);

        internal OperationPolicyContext Context() => new()
        {
            TenantId = "tenant-a",
            PrincipalId = "actor-1",
            OperationInstanceId = "opinst-1",
            CorrelationId = "corr-1",
        };

        internal OperationRequest Request(string operationId, params (string Key, string Value)[] extra)
        {
            var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["intentId"] = "intent-buffer",
                ["schema"] = "honua_data",
                ["targetId"] = Reference.TargetId,
                ["jobId"] = Reference.JobId,
                ["resultPackageId"] = Reference.ResultPackageId,
                ["artifactId"] = Reference.ArtifactId,
                ["referenceSha256"] = Reference.ReferenceSha256,
                ["targetSrid"] = "4326",
            };
            foreach (var (key, value) in extra)
            {
                parameters[key] = value;
            }
            return new OperationRequest { OperationId = operationId, Parameters = parameters };
        }

        internal async Task<OperationRequest> PrepareProposalAsync()
        {
            var preview = await Preview.SubmitAsync(Request(StagedImportOperation.PreviewOperationId), Context(),
                CancellationToken.None);
            var fingerprint = preview.Result!.Details[StagedImportOperation.PreviewFingerprintParameter];
            return await Import.PrepareAsync(Request(StagedImportOperation.ImportOperationId,
                (StagedImportOperation.PreviewFingerprintParameter, fingerprint)), Context(), CancellationToken.None);
        }
    }

    private sealed class SteppedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
