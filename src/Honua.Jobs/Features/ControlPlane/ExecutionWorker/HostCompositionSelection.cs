// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Configuration;

namespace Honua.ControlPlane;

internal enum HostCompositionProfile
{
    Server = 0,

    ExecutionWorker = 1,
}

internal sealed record ExecutionWorkerLaunch
{
    public const string OperationIdVariable = "HONUA_OPERATION_ID";

    public const string JobKindVariable = "HONUA_JOB_KIND";

    public const string WorkloadNameVariable = "HONUA_WORKLOAD_NAME";

    public const string WorkloadIdVariable = "HONUA_WORKLOAD_ID";

    public const string RuntimeProfileVariable = "HONUA_RUNTIME_PROFILE";

    public const string ContractVersionVariable = "HONUA_CONTRACT_VERSION";

    public const string ExecutionAttemptVariable = "HONUA_EXECUTION_ATTEMPT";

    public const int MaxSupportedContractVersion = 1;

    public required string OperationId { get; init; }

    public ExecutionJobKind? JobKind { get; init; }

    public string? WorkloadName { get; init; }

    public string? WorkloadId { get; init; }

    public string? RuntimeProfile { get; init; }

    public int ContractVersion { get; init; } = 1;

    public int? ExecutionAttempt { get; init; }
}

internal static class ExecutionWorkerLaunchEnvironment
{
    // Workload environment passthrough must not set the launch variable names, or a worker could claim a different job than the provider was asked to run.
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

    public static bool IsReserved(string name) => ReservedNames.Contains(name);

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

internal sealed record HostCompositionSelection(HostCompositionProfile Profile, ExecutionWorkerLaunch? Launch)
{
    public static HostCompositionSelection Server { get; } = new(HostCompositionProfile.Server, null);

    public bool IsExecutionWorker => Profile == HostCompositionProfile.ExecutionWorker;

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
