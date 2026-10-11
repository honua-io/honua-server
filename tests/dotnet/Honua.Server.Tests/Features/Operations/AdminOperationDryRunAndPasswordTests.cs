// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

/// <summary>
/// Unit coverage for dry-run dispatch, case-insensitive inline passwords, and
/// queued connect/import status mapping (honua-server#5625).
/// </summary>
public sealed class AdminOperationDryRunAndPasswordTests
{
    [UnitTest]
    public async Task DryRun_InvokesMarkerExecutor_AndSkipsOtherSupportsDryRunExecutors()
    {
        var marker = new MarkerProbeExecutor("admin.marker", OperationHandleStatus.Failed);
        var plain = new ProbeExecutor("admin.plain", OperationHandleStatus.Failed);
        var catalog = new OperationCatalog(
            [
                new SingleDescriptorProvider(Descriptor("admin.marker")),
                new SingleDescriptorProvider(Descriptor("admin.plain")),
            ],
            TimeProvider.System);
        var dispatcher = new OperationDispatcher(
            catalog,
            [marker, plain],
            new AllowAllPolicyDecisionPoint(),
            TimeProvider.System);

        var marked = await dispatcher.SubmitAsync(
            new OperationRequest { OperationId = "admin.marker", DryRun = true },
            new OperationPolicyContext(),
            CancellationToken.None);
        var synthetic = await dispatcher.SubmitAsync(
            new OperationRequest { OperationId = "admin.plain", DryRun = true },
            new OperationPolicyContext(),
            CancellationToken.None);

        marker.SubmitCalls.Should().Be(1);
        marked.Status.Should().Be(OperationHandleStatus.Failed);
        plain.SubmitCalls.Should().Be(0);
        synthetic.Status.Should().Be(OperationHandleStatus.Completed);
        synthetic.Reason.Should().Be("Dry run completed; no actuator was invoked.");
    }

    [UnitTest]
    public async Task Validate_PasswordAnyCase_IsRejected()
    {
        var executor = Executor();

        var validation = await executor.ValidateAsync(
            new OperationRequest
            {
                OperationId = "admin.connections.test-draft",
                Parameters = new Dictionary<string, string?> { ["Password"] = "secret" },
            });

        validation.IsValid.Should().BeFalse();
        validation.Messages.Should().Contain("Inline passwords cannot be persisted for approval; use secretReference.");
    }

    [UnitTest]
    public void From_InlinePassword_ThrowsBeforeCopyingParameters()
    {
        var act = () => AdminConnectImportApprovalPayload.From(
            new OperationRequest
            {
                OperationId = "admin.connections.test-draft",
                Parameters = new Dictionary<string, string?> { ["PASSWORD"] = "secret" },
            },
            new OperationPolicyContext());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Inline passwords cannot be persisted for approval; use secretReference.");
    }

    [UnitTest]
    public void From_EmptyPassword_IsDropped()
    {
        var payload = AdminConnectImportApprovalPayload.From(
            new OperationRequest
            {
                OperationId = "admin.connections.test-draft",
                Parameters = new Dictionary<string, string?> { ["Password"] = "" },
            },
            new OperationPolicyContext());

        payload.Parameters.Keys.Should().NotContain(key =>
            string.Equals(key, "password", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public async Task GetStatus_QueuedImport_MapsJobProgress()
    {
        var jobs = Substitute.For<IImportJobService>();
        jobs.GetProgressAsync("job-1", Arg.Any<CancellationToken>()).Returns(new ImportProgress
        {
            JobId = "job-1",
            Status = ImportStatus.Completed,
            TableName = "features",
            Format = SupportedFileFormat.GeoJson,
        });
        var executor = Executor("admin.import.upload-url", jobs);

        var completed = await executor.GetStatusAsync(Handle(OperationHandleStatus.Queued, "job-1"));

        completed.Status.Should().Be(OperationHandleStatus.Completed);

        jobs.GetProgressAsync("job-2", Arg.Any<CancellationToken>()).Returns((ImportProgress?)null);
        var missing = await executor.GetStatusAsync(Handle(OperationHandleStatus.Queued, "job-2"));
        missing.Status.Should().Be(OperationHandleStatus.Indeterminate);
        missing.Reason.Should().Be("Queued import job is no longer present.");

        var running = await executor.GetStatusAsync(Handle(OperationHandleStatus.Running, "job-3"));
        running.Status.Should().Be(OperationHandleStatus.Running);
        await jobs.DidNotReceive().GetProgressAsync("job-3", Arg.Any<CancellationToken>());
    }

    private static AdminConnectImportOperationExecutor Executor(
        string operationId = "admin.connections.test-draft",
        IImportJobService? jobs = null)
    {
        var definition = AdminConnectImportOperationCatalog.Definitions.Single(item => item.OperationId == operationId);
        return new AdminConnectImportOperationExecutor(
            definition,
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IAdminApiKeyStore>(),
            TimeProvider.System,
            new OperationLineageAttestationStore(TimeProvider.System),
            jobs);
    }

    private static OperationHandle Handle(OperationHandleStatus status, string? jobId) => new()
    {
        OperationInstanceId = "inst-1",
        OperationId = "admin.import.upload-url",
        Status = status,
        CorrelationId = "corr-1",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
        JobId = jobId,
    };

    private static OperationDescriptor Descriptor(string operationId) => new()
    {
        OperationId = operationId,
        ProviderId = "test",
        Title = operationId,
        Description = operationId,
        Category = "admin",
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.None,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.None,
            SideEffectClass = OperationSideEffectClass.ReadOnly,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = true,
        },
    };

    private sealed class SingleDescriptorProvider(OperationDescriptor descriptor) : IOperationDescriptorProvider
    {
        public string ProviderId => descriptor.ProviderId;

        public Task<IReadOnlyList<IOperationDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IOperationDescriptor>>([descriptor]);
    }

    private class ProbeExecutor(string operationId, OperationHandleStatus status) : IOperationExecutor
    {
        public string OperationId { get; } = operationId;

        public int SubmitCalls { get; private set; }

        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(new OperationHandle
            {
                OperationInstanceId = "probe",
                OperationId = OperationId,
                Status = status,
                CorrelationId = "probe",
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MarkerProbeExecutor(string operationId, OperationHandleStatus status)
        : ProbeExecutor(operationId, status), IOperationDryRunExecutor;
}
