// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.Batch;
using Amazon.Batch.Model;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Provider-neutral environment variable override for an AWS Batch container.
/// </summary>
internal sealed record AwsBatchEnvironmentOverride(string Name, string Value);

/// <summary>
/// Submission parameters for an AWS Batch job.
/// </summary>
internal sealed record AwsBatchJobSubmission
{
    public required string JobName { get; init; }

    public required string JobDefinition { get; init; }

    public required string JobQueue { get; init; }

    public IReadOnlyList<AwsBatchEnvironmentOverride> EnvironmentOverrides { get; init; } = [];

    public int? Vcpus { get; init; }

    public int? MemoryMib { get; init; }

    public int? GpuCount { get; init; }

    public int? AttemptDurationSeconds { get; init; }

    public int? RetryAttempts { get; init; }

    public string? ShareIdentifier { get; init; }
}

/// <summary>
/// Result of submitting an AWS Batch job.
/// </summary>
internal sealed record AwsBatchSubmitResult
{
    public required string JobId { get; init; }

    public string? JobArn { get; init; }

    public required string JobName { get; init; }
}

/// <summary>
/// Snapshot of an AWS Batch job observed by DescribeJobs.
/// </summary>
internal sealed record AwsBatchJobState
{
    public required string JobId { get; init; }

    public string? JobArn { get; init; }

    public string? Status { get; init; }

    public string? StatusReason { get; init; }

    public int? ExitCode { get; init; }
}

internal interface IAwsBatchJobClient
{
    Task<AwsBatchSubmitResult> SubmitJobAsync(
        AwsBatchJobSubmission submission,
        string? region,
        CancellationToken cancellationToken = default);

    Task<AwsBatchJobState?> DescribeJobAsync(
        string jobId,
        string? region,
        CancellationToken cancellationToken = default);

    Task CancelJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default);

    Task TerminateJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default);
}

internal sealed class AwsSdkBatchJobClient : IAwsBatchJobClient
{
    public async Task<AwsBatchSubmitResult> SubmitJobAsync(
        AwsBatchJobSubmission submission,
        string? region,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        using var client = CreateClient(region);
        var request = new SubmitJobRequest
        {
            JobName = submission.JobName,
            JobDefinition = submission.JobDefinition,
            JobQueue = submission.JobQueue,
            ContainerOverrides = BuildContainerOverrides(submission)
        };

        if (submission.AttemptDurationSeconds.HasValue)
        {
            request.Timeout = new JobTimeout
            {
                AttemptDurationSeconds = submission.AttemptDurationSeconds.Value
            };
        }

        if (submission.RetryAttempts.HasValue && submission.RetryAttempts.Value > 0)
        {
            request.RetryStrategy = new RetryStrategy
            {
                Attempts = submission.RetryAttempts.Value
            };
        }

        if (!string.IsNullOrWhiteSpace(submission.ShareIdentifier))
        {
            request.ShareIdentifier = submission.ShareIdentifier;
        }

        var response = await client.SubmitJobAsync(request, cancellationToken).ConfigureAwait(false);
        return new AwsBatchSubmitResult
        {
            JobId = response.JobId,
            JobArn = response.JobArn,
            JobName = response.JobName
        };
    }

    public async Task<AwsBatchJobState?> DescribeJobAsync(
        string jobId,
        string? region,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var client = CreateClient(region);
        var response = await client.DescribeJobsAsync(
            new DescribeJobsRequest { Jobs = [jobId] },
            cancellationToken).ConfigureAwait(false);

        var detail = response.Jobs?.FirstOrDefault();
        if (detail == null)
        {
            return null;
        }

        var latestExitCode = detail.Attempts?
            .Where(attempt => attempt.Container?.ExitCode != null)
            .Select(attempt => (int?)attempt.Container.ExitCode)
            .LastOrDefault();

        return new AwsBatchJobState
        {
            JobId = detail.JobId,
            JobArn = detail.JobArn,
            Status = detail.Status?.Value,
            StatusReason = detail.StatusReason,
            ExitCode = latestExitCode
        };
    }

    public async Task CancelJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var client = CreateClient(region);
        await client.CancelJobAsync(
            new CancelJobRequest
            {
                JobId = jobId,
                Reason = reason ?? "Cancelled by Honua control plane"
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TerminateJobAsync(
        string jobId,
        string reason,
        string? region,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var client = CreateClient(region);
        await client.TerminateJobAsync(
            new TerminateJobRequest
            {
                JobId = jobId,
                Reason = reason ?? "Terminated by Honua control plane"
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static ContainerOverrides BuildContainerOverrides(AwsBatchJobSubmission submission)
    {
        var overrides = new ContainerOverrides();

        if (submission.EnvironmentOverrides.Count > 0)
        {
            overrides.Environment = submission.EnvironmentOverrides
                .Select(entry => new Amazon.Batch.Model.KeyValuePair { Name = entry.Name, Value = entry.Value })
                .ToList();
        }

        var resourceRequirements = new List<ResourceRequirement>();
        if (submission.Vcpus.HasValue && submission.Vcpus.Value > 0)
        {
            resourceRequirements.Add(new ResourceRequirement
            {
                Type = ResourceType.VCPU,
                Value = submission.Vcpus.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        if (submission.MemoryMib.HasValue && submission.MemoryMib.Value > 0)
        {
            resourceRequirements.Add(new ResourceRequirement
            {
                Type = ResourceType.MEMORY,
                Value = submission.MemoryMib.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        if (submission.GpuCount.HasValue && submission.GpuCount.Value > 0)
        {
            resourceRequirements.Add(new ResourceRequirement
            {
                Type = ResourceType.GPU,
                Value = submission.GpuCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        if (resourceRequirements.Count > 0)
        {
            overrides.ResourceRequirements = resourceRequirements;
        }

        return overrides;
    }

    private static AmazonBatchClient CreateClient(string? region)
    {
        if (!string.IsNullOrWhiteSpace(region))
        {
            return new AmazonBatchClient(RegionEndpoint.GetBySystemName(region));
        }

        return new AmazonBatchClient();
    }
}
