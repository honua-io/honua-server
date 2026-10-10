// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Configuration;

namespace Honua.ControlPlane;

/// <summary>
/// The composition profile a Honua server process runs under. It is resolved once, at process
/// start, from the launch environment and decides what the composition root registers; services
/// never consult the launch environment themselves.
/// </summary>
internal enum HostCompositionProfile
{
    /// <summary>
    /// The serving/control-plane host: HTTP surface, reconcilers, the job dispatcher that submits
    /// execution jobs to batch compute backends, the proposal gateway and every scheduled
    /// background service.
    /// </summary>
    Server = 0,

    /// <summary>
    /// A run-to-completion execution worker that a batch compute backend (AWS Batch, Azure Batch,
    /// a Kubernetes Job, the local process pool) launched for exactly one execution job. It runs
    /// that job's executor, reports the outcome through the durable job store and exits. It never
    /// dispatches jobs, reconciles the control plane or serves HTTP.
    /// </summary>
    ExecutionWorker = 1,
}

/// <summary>
/// The per-job launch contract a batch compute backend injects into the container or child process
/// it starts (the <c>HONUA_*</c> container overrides of <c>AwsBatchComputeBackend</c>,
/// <c>AzureBatchComputeBackend</c>, <c>KubernetesJobBatchComputeBackend</c> and
/// <c>LocalProcessPoolBatchComputeBackend</c>).
/// </summary>
internal sealed record ExecutionWorkerLaunch
{
    /// <summary>The operation id of the one execution job the worker was launched for.</summary>
    public const string OperationIdVariable = "HONUA_OPERATION_ID";

    /// <summary>The <see cref="ExecutionJobKind"/> name of the launched job.</summary>
    public const string JobKindVariable = "HONUA_JOB_KIND";

    /// <summary>The display name of the execution workload the job was routed to.</summary>
    public const string WorkloadNameVariable = "HONUA_WORKLOAD_NAME";

    /// <summary>The id of the execution workload the job was routed to.</summary>
    public const string WorkloadIdVariable = "HONUA_WORKLOAD_ID";

    /// <summary>The runtime profile the job requires.</summary>
    public const string RuntimeProfileVariable = "HONUA_RUNTIME_PROFILE";

    /// <summary>The serving-to-worker job-contract version (ADR-0060 principle #3b).</summary>
    public const string ContractVersionVariable = "HONUA_CONTRACT_VERSION";

    /// <summary>
    /// The highest serving-to-worker job-contract version this image's execution worker can run.
    /// A launch or a job record that names a higher version fails closed instead of running a job
    /// whose shape this worker does not understand.
    /// </summary>
    public const int MaxSupportedContractVersion = 1;

    public required string OperationId { get; init; }

    public ExecutionJobKind? JobKind { get; init; }

    public string? WorkloadName { get; init; }

    public string? WorkloadId { get; init; }

    public string? RuntimeProfile { get; init; }

    public int ContractVersion { get; init; } = 1;
}

/// <summary>
/// The resolved composition switch for one process: the profile and, for an execution worker,
/// the launch it runs.
/// </summary>
internal sealed record HostCompositionSelection(HostCompositionProfile Profile, ExecutionWorkerLaunch? Launch)
{
    /// <summary>The serving host selection.</summary>
    public static HostCompositionSelection Server { get; } = new(HostCompositionProfile.Server, null);

    /// <summary>Whether this process is a single-job execution worker.</summary>
    public bool IsExecutionWorker => Profile == HostCompositionProfile.ExecutionWorker;

    /// <summary>
    /// Resolves the composition profile. A process is an execution worker exactly when a batch
    /// compute backend launched it for a job, which every backend signals by injecting
    /// <see cref="ExecutionWorkerLaunch.OperationIdVariable"/>; any other process is the server.
    /// A launch whose companion values cannot be parsed refuses to start rather than guessing.
    /// </summary>
    public static HostCompositionSelection Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var operationId = configuration[ExecutionWorkerLaunch.OperationIdVariable];
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return Server;
        }

        ExecutionJobKind? jobKind = null;
        var jobKindText = configuration[ExecutionWorkerLaunch.JobKindVariable];
        if (!string.IsNullOrWhiteSpace(jobKindText))
        {
            // Backends inject the enum NAME (ExecutionJobKind.ToString()); a numeric value is not
            // part of the contract and must not alias a kind.
            if (char.IsDigit(jobKindText.Trim()[0])
                || !Enum.TryParse<ExecutionJobKind>(jobKindText.Trim(), ignoreCase: false, out var parsedKind)
                || !Enum.IsDefined(parsedKind))
            {
                throw new InvalidOperationException(
                    $"Execution worker launch for operation '{operationId.Trim()}' names unknown job kind "
                    + $"'{jobKindText.Trim()}' in {ExecutionWorkerLaunch.JobKindVariable}.");
            }

            jobKind = parsedKind;
        }

        var contractVersion = 1;
        var contractVersionText = configuration[ExecutionWorkerLaunch.ContractVersionVariable];
        if (!string.IsNullOrWhiteSpace(contractVersionText)
            && (!int.TryParse(contractVersionText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out contractVersion)
                || contractVersion < 1))
        {
            throw new InvalidOperationException(
                $"Execution worker launch for operation '{operationId.Trim()}' carries an invalid "
                + $"{ExecutionWorkerLaunch.ContractVersionVariable} value '{contractVersionText.Trim()}'.");
        }

        return new HostCompositionSelection(
            HostCompositionProfile.ExecutionWorker,
            new ExecutionWorkerLaunch
            {
                OperationId = operationId.Trim(),
                JobKind = jobKind,
                WorkloadName = Normalize(configuration[ExecutionWorkerLaunch.WorkloadNameVariable]),
                WorkloadId = Normalize(configuration[ExecutionWorkerLaunch.WorkloadIdVariable]),
                RuntimeProfile = Normalize(configuration[ExecutionWorkerLaunch.RuntimeProfileVariable]),
                ContractVersion = contractVersion,
            });
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
