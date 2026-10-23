// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Import.FileImport.Resumable;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Import;
using Honua.Server.Features.Operations;
using StackExchange.Redis;

namespace Honua.Server.Features.AgentWorkspace;

// Host-owned local composition only. Remote workers need shared source storage
// and remote execution evidence; this adapter must never certify that topology.
internal sealed class LocalDatasetImportSubmissionReadiness(IServiceProvider services) : IDatasetImportSubmissionReadiness
{
    internal string? LastFailure { get; private set; }
    internal Exception? LastException { get; private set; }
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly Type[] RequiredServices =
    [
        typeof(DatasetImportLocalStorage), typeof(ImportStagedSourceStorage), typeof(ImportScratchStorage),
        typeof(IImportStorageBudget), typeof(IImportProcessingScratchFactory), typeof(ImportUploadCoordinator),
        typeof(DatasetImportSubmissionService), typeof(IDatasetImportAdmissionProvider), typeof(IDatasetImportAuthorityDescriptorReader),
        typeof(IImportJobService), typeof(IFileImportService), typeof(BackgroundImportRunnerDecision),
        typeof(IJobQueue), typeof(IExecutionJobStore), typeof(IImportExecutionCommitAuthority), typeof(IImportCommittedResultReader),
        typeof(IJobExecutionLeaseAuthority), typeof(JobExecutionLeaseAuthority),
        typeof(IJobExecutionLeasePolicy), typeof(IJobExecutionLeaseObservationPolicy),
        typeof(IJobExecutionLeaseScopeProvider), typeof(IJobExecutionLeaseObservationScopeProvider)
    ];

    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastFailure = null;
        LastException = null;
        var registrations = services.GetService<IServiceProviderIsService>();
        if (registrations is null) return Unready("service-registry-unavailable");
        var missing = RequiredServices.FirstOrDefault(type => !registrations.IsService(type));
        if (missing is not null) return Unready("missing-" + missing.Name);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);
        var token = deadline.Token;
        try
        {
            var storage = services.GetRequiredService<DatasetImportLocalStorage>();
            if (!await storage.IsReadyAsync(token).AsTask().WaitAsync(token).ConfigureAwait(false))
                return Unready("local-storage-not-ready");
            var policy = services.GetRequiredService<IJobExecutionLeasePolicy>();
            (string Reason, bool Matches)[] bindings =
            [
                ("source", ReferenceEquals(storage.Source, services.GetRequiredService<ImportStagedSourceStorage>())),
                ("scratch", ReferenceEquals(storage.Scratch, services.GetRequiredService<ImportScratchStorage>())),
                ("budget", ReferenceEquals(storage.Domain, services.GetRequiredService<IImportStorageBudget>())),
                ("processing", ReferenceEquals(storage.Processing, services.GetRequiredService<IImportProcessingScratchFactory>())),
                ("runner", services.GetRequiredService<BackgroundImportRunnerDecision>().Mode == BackgroundImportRunnerMode.DurableLocal),
                ("job-service", services.GetRequiredService<IImportJobService>() is DurableImportJobService),
                ("commit-authority", services.GetRequiredService<IImportExecutionCommitAuthority>() is FileImportExecutionCommitAuthority),
                ("lease-facade", services.GetRequiredService<IJobExecutionLeaseAuthority>() is JobExecutionLeaseAuthorityFacade),
                ("lease-policy", policy is ExecutionJobLeasePolicy && ReferenceEquals(policy, services.GetRequiredService<IJobExecutionLeaseObservationPolicy>()))
            ];
            foreach (var binding in bindings)
                if (!binding.Matches) return Unready("local-composition-" + binding.Reason + "-mismatch");

            var execution = services.GetServices<IJobExecutionLeaseScopeProvider>()
                .Where(provider => provider.Kind == ExecutionJobKind.FileImport).ToArray();
            var observation = services.GetServices<IJobExecutionLeaseObservationScopeProvider>()
                .OfType<FileImportExecutionLeaseScopeProvider>().ToArray();
            if (execution.Length != 1 || execution[0] is not FileImportExecutionLeaseScopeProvider
                || observation.Length != 1 || !ReferenceEquals(execution[0], observation[0])) return Unready("file-import-scope-provider-mismatch");

            // Construct the actual scoped execution graph, rather than trusting
            // registrations that cannot be activated on the worker's path.
            _ = services.GetRequiredService<IFileImportService>();
            _ = services.GetRequiredService<JobExecutionLeaseAuthority>();
            if (!services.GetRequiredService<ImportUploadCoordinator>().IsBoundToStorage(storage.Source)) return Unready("source-storage-mismatch");
            var queue = services.GetRequiredService<IJobQueue>();
            var jobs = services.GetRequiredService<IExecutionJobStore>();
            if (!await AnyWorkerReadyAsync(queue, jobs, token).ConfigureAwait(false)) return Unready("local-worker-not-ready");

            // Exercise the configured destination and canonical receipt table.
            // A random, nonexistent operation reads no user data and writes no
            // receipt; an absent migration or unavailable database fails closed.
            var result = await services.GetRequiredService<IImportCommittedResultReader>()
                .TryReadAsync(Guid.NewGuid().ToString("N"), "v1:sha256:" + new string('0', 64), token)
                .WaitAsync(token).ConfigureAwait(false);
            var ready = result is null && await storage.IsReadyAsync(token).AsTask().WaitAsync(token).ConfigureAwait(false)
                && await AnyWorkerReadyAsync(queue, jobs, token).ConfigureAwait(false);
            return ready || Unready("readiness-changed-during-probe");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unready("readiness-probe-deadline");
        }
        catch (Exception exception) when (exception is IOException or RedisException or DbException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
        {
            LastException = exception;
            return Unready("readiness-probe-" + exception.GetType().Name);
        }
    }

    private bool Unready(string reason)
    {
        LastFailure = reason;
        return false;
    }

    private async ValueTask<bool> AnyWorkerReadyAsync(IJobQueue queue, IExecutionJobStore jobs, CancellationToken cancellationToken)
    {
        foreach (var worker in services.GetServices<IHostedService>().OfType<JobExecutionService>())
        {
            if (await worker.IsReadyForAsync(queue, jobs, ExecutionJobKind.FileImport, RuntimeProfiles.Managed, cancellationToken).ConfigureAwait(false))
                return true;
        }
        return false;
    }
}
