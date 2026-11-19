// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

// An approval proposal's submitter snapshot is used on resume only when its seal verifies for that proposal.
public sealed partial class GeoprocessingJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ResumeApprovedJob_SealedSnapshot_ResumesWithIt()
    {
        var sealer = new BindingSealer();
        var payload = SealedPayload(sealer, "idem-sealed");

        var job = await SealingService(sealer).ResumeApprovedJobAsync(payload);

        job.Audit.SubmitterSecurityContext.Should().BeSameAs(payload.SubmitterSecurityContext);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ResumeApprovedJob_SnapshotEditedAfterSealing_IsRefusedAndCreatesNoJob()
    {
        var sealer = new BindingSealer();
        var sealedPayload = SealedPayload(sealer, "idem-edited");
        var payload = sealedPayload with
        {
            SubmitterSecurityContext = sealedPayload.SubmitterSecurityContext! with
            {
                Claims = [.. sealedPayload.SubmitterSecurityContext.Claims, new JobSecurityClaim(ClaimTypes.Role, "admin")],
            },
        };

        var act = async () => await SealingService(sealer).ResumeApprovedJobAsync(payload);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await _jobStore.DidNotReceive().TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ResumeApprovedJob_SealFromAnotherProposal_IsRefused()
    {
        var sealer = new BindingSealer();
        var payload = SealedPayload(sealer, "idem-original") with { IdempotencyKey = "idem-other" };

        var act = async () => await SealingService(sealer).ResumeApprovedJobAsync(payload);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ResumeApprovedJob_PlanRewrittenAfterSealing_IsRefused()
    {
        var sealer = new BindingSealer();
        var sealedPayload = SealedPayload(sealer, "idem-plan");
        var payload = sealedPayload with { Plan = sealedPayload.Plan with { IntentId = "rewritten" } };

        var act = async () => await SealingService(sealer).ResumeApprovedJobAsync(payload);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ResumeApprovedJob_UnsealedSnapshotOnASealingHost_IsRefused()
    {
        var payload = new GeoprocessExecutionPayload
        {
            Plan = CreateImportPlan(),
            IdempotencyKey = "idem-unsealed",
            RequestedBy = "subject-123",
            SubmitterSecurityContext = CreateSubmitterSecurityContext(),
        };

        var act = async () => await SealingService(new BindingSealer()).ResumeApprovedJobAsync(payload);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    private static GeoprocessExecutionPayload SealedPayload(BindingSealer sealer, string idempotencyKey)
    {
        var payload = new GeoprocessExecutionPayload
        {
            Plan = CreateImportPlan(),
            IdempotencyKey = idempotencyKey,
            RequestedBy = "subject-123",
            SubmitterSecurityContext = CreateSubmitterSecurityContext(),
        };
        return payload with
        {
            SubmitterSecurityContextSeal = sealer.Seal(
                JobSecurityContextDigest.Binding("honua.geoprocess-proposal.v1", payload.RequestedBy, idempotencyKey, payload.SerializePlan()),
                payload.SubmitterSecurityContext),
        };
    }

    private GeoprocessingJobService SealingService(IJobSecurityContextSealer sealer)
    {
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return new GeoprocessingJobService(
            _progressStore, [_cancellationNotifier],
            _authEvaluator, _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            DefaultExecutorOptions,
            _jobStore, _jobQueue,
            resultPackageStore: _resultPackageStore,
            jobSecurityContextSealer: sealer);
    }

    // Stands in for the key-ring sealer: the seal is a fingerprint of the binding and every snapshot field.
    private sealed class BindingSealer : IJobSecurityContextSealer
    {
        public string Seal(string binding, JobSecurityContext context) => Fingerprint(binding, context);

        public bool Verify(string binding, JobSecurityContext context, string? seal)
            => string.Equals(seal, Fingerprint(binding, context), StringComparison.Ordinal);

        private static string Fingerprint(string binding, JobSecurityContext context)
            => string.Join('|', binding, context.PrincipalId, context.TenantId, context.RoleClaimType, context.OwnerActorId,
                context.WorkspaceOwnerId, string.Join(';', context.Claims.Select(claim => $"{claim.Type}={claim.Value}")));
    }
}
