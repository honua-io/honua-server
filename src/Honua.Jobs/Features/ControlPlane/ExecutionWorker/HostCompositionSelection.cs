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
    /// The submission attempt (the durable record's <c>AttemptCount</c> once the submission is
    /// recorded) the provider job was launched for. A worker only claims the attempt it was
    /// launched for, so a stale or provider-retried container can never run a newer attempt.
    /// </summary>
    public const string ExecutionAttemptVariable = "HONUA_EXECUTION_ATTEMPT";

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

    /// <summary>The launched submission attempt, when the submitting server stamped one.</summary>
    public int? ExecutionAttempt { get; init; }
}

/// <summary>
/// Builds the launch environment every batch compute backend injects, so the worker-mode switch
/// sees the same contract whichever provider launched the process.
/// </summary>
internal static class ExecutionWorkerLaunchEnvironment
{
    /// <summary>
    /// The launch variable names. Workload <c>env.*</c> passthrough may never set them: a workload
    /// that could rename the operation would make a worker claim a different job than the one the
    /// provider was asked to run.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.Ordinal)
    {
        ExecutionWorkerLaunch.OperationIdVariable,
        ExecutionWorkerLaunch.JobKindVariable,
        ExecutionWorkerLaunch.WorkloadNameVariable,
        ExecutionWorkerLaunch.WorkloadIdVariable,
        ExecutionWorkerLaunch.RuntimeProfileVariable,
        ExecutionWorkerLaunch.ContractVersionVariable,
        ExecutionWorkerLaunch.ExecutionAttemptVariable,
    };

    /// <summary>Whether a workload passthrough name is a reserved launch variable.</summary>
    public static bool IsReserved(string name) => ReservedNames.Contains(name);

    /// <summary>
    /// The launch variables for <paramref name="job"/>, to be stamped AFTER any workload passthrough.
    /// </summary>
    /// <param name="job">The record being launched.</param>
    /// <param name="executionAttempt">
    /// The attempt this launch runs. A backend's <c>StartAsync</c> receives the record before the
    /// submission is counted, so it passes <c>job.AttemptCount + 1</c>.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(ExecutionJobRecord job, int executionAttempt)
    {
        ArgumentNullException.ThrowIfNull(job);
        var variables = new List<KeyValuePair<string, string>>
        {
            new(ExecutionWorkerLaunch.OperationIdVariable, job.OperationId),
            new(ExecutionWorkerLaunch.WorkloadNameVariable, job.Spec.WorkloadName),
            new(ExecutionWorkerLaunch.JobKindVariable, job.Spec.Kind.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(job.Spec.WorkloadId))
        {
            variables.Add(new(ExecutionWorkerLaunch.WorkloadIdVariable, job.Spec.WorkloadId));
        }

        if (!string.IsNullOrWhiteSpace(job.Spec.RuntimeProfile))
        {
            variables.Add(new(ExecutionWorkerLaunch.RuntimeProfileVariable, job.Spec.RuntimeProfile));
        }

        variables.Add(new(
            ExecutionWorkerLaunch.ExecutionAttemptVariable,
            Math.Max(1, executionAttempt).ToString(CultureInfo.InvariantCulture)));
        variables.Add(new(
            ExecutionWorkerLaunch.ContractVersionVariable,
            job.Spec.ContractVersion.ToString(CultureInfo.InvariantCulture)));
        return variables;
    }
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

        int? executionAttempt = null;
        var attemptText = configuration[ExecutionWorkerLaunch.ExecutionAttemptVariable];
        if (!string.IsNullOrWhiteSpace(attemptText))
        {
            if (!int.TryParse(attemptText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var attempt)
                || attempt < 1)
            {
                throw new InvalidOperationException(
                    $"Execution worker launch for operation '{operationId.Trim()}' carries an invalid "
                    + $"{ExecutionWorkerLaunch.ExecutionAttemptVariable} value '{attemptText.Trim()}'.");
            }

            executionAttempt = attempt;
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
                ExecutionAttempt = executionAttempt,
            });
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
