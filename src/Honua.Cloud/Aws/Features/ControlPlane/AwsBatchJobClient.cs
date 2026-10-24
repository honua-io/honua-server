// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.Batch;
using Amazon.Batch.Model;
using Honua.Cloud.Aws.Features;

namespace Honua.ControlPlane;

/// <summary>
/// Provider-neutral environment variable override for an AWS Batch container.
/// </summary>
internal sealed record AwsBatchEnvironmentOverride(string Name, string Value);

/// <summary>
/// Submission parameters for an AWS Batch job.
/// </summary>
internal sealed record AwsBatchJobSubmission
{
    public bool RequireCredentialFreeCustomCode { get; init; }

    public string? ExpectedCustomCodeImage { get; init; }

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
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);

    Task<AwsBatchJobState?> DescribeJobAsync(
        string jobId,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AwsBatchJobState>> ListJobsByNameAsync(
        string jobQueue,
        string jobName,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);

    Task CancelJobAsync(
        string jobId,
        string reason,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);

    Task TerminateJobAsync(
        string jobId,
        string reason,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);
}

internal sealed class AwsSdkBatchJobClient : IAwsBatchJobClient
{
    public async Task<AwsBatchSubmitResult> SubmitJobAsync(
        AwsBatchJobSubmission submission,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // SubmitJob has no idempotency token. Retrying an ambiguous response inside the
        // SDK can start two containers before our durable discovery path gets control.
        using var client = CreateClient(region, serviceUrl, submitting: true);
        if (submission.RequireCredentialFreeCustomCode)
        {
            var definitions = await client.DescribeJobDefinitionsAsync(new DescribeJobDefinitionsRequest
            {
                JobDefinitions = [submission.JobDefinition]
            }, cancellationToken).ConfigureAwait(false);
            var definition = definitions.JobDefinitions?.SingleOrDefault();
            if (definition is null || !string.Equals(definition.JobDefinitionArn, submission.JobDefinition, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Custom-code execution requires an exact verified AWS Batch job-definition revision.");
            }
            ValidateCredentialFreeCustomCodeDefinition(definition, submission.EnvironmentOverrides);
            if (string.IsNullOrWhiteSpace(submission.ExpectedCustomCodeImage) ||
                !submission.ExpectedCustomCodeImage.Contains("@sha256:", StringComparison.Ordinal) ||
                !string.Equals(definition.ContainerProperties.Image, submission.ExpectedCustomCodeImage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Custom-code provider image must match the admitted immutable worker digest.");
            }
        }
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

    internal static void ValidateCredentialFreeCustomCodeDefinition(
        JobDefinition definition, IReadOnlyList<AwsBatchEnvironmentOverride> overrides)
    {
        var container = definition.ContainerProperties;
        if (definition.PlatformCapabilities is not { Count: 1 } ||
            !string.Equals(definition.PlatformCapabilities[0].ToString(), "FARGATE", StringComparison.Ordinal) ||
            definition.EcsProperties is not null || definition.EksProperties is not null || definition.NodeProperties is not null ||
            container is null || !string.IsNullOrWhiteSpace(container.JobRoleArn) ||
            container.Privileged == true || container.User is not "1001" and not "1001:1001" ||
            container.Secrets is { Count: > 0 } || container.Volumes is { Count: > 0 } ||
            container.Environment?.Any(entry => IsCredentialEnvironmentName(entry.Name)) == true ||
            overrides.Any(entry => IsCredentialEnvironmentName(entry.Name)))
        {
            throw new InvalidOperationException("Custom-code execution requires Fargate, the sanctioned non-root user, no task role, no mounted volumes or injected secrets, and no cloud credential environment.");
        }
    }

    internal static bool IsCredentialEnvironmentName(string name)
        => (name.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) &&
            name is not "AWS_REGION" and not "AWS_DEFAULT_REGION" and not "AWS_EC2_METADATA_DISABLED") ||
            name.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase) ||
            name is "CLOUDSDK_CONFIG";

    public async Task<AwsBatchJobState?> DescribeJobAsync(
        string jobId,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var client = CreateClient(region, serviceUrl);
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

    public async Task<IReadOnlyList<AwsBatchJobState>> ListJobsByNameAsync(
        string jobQueue,
        string jobName,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        using var client = CreateClient(region, serviceUrl);
        var states = new List<AwsBatchJobState>();
        string? nextToken = null;

        // ListJobs is paginated. The sole consumer reads the newest match, but a busy queue can push
        // recent jobs past the first page, so walk every page (NextToken) until exhausted rather than
        // silently truncating to the first response.
        do
        {
            var response = await client.ListJobsAsync(
                new ListJobsRequest
                {
                    JobQueue = jobQueue,
                    NextToken = nextToken,
                    Filters =
                    [
                        new Amazon.Batch.Model.KeyValuesPair
                        {
                            Name = "JOB_NAME",
                            Values = [jobName]
                        }
                    ]
                },
                cancellationToken).ConfigureAwait(false);

            if (response.JobSummaryList is { Count: > 0 } summaries)
            {
                foreach (var summary in summaries)
                {
                    states.Add(new AwsBatchJobState
                    {
                        JobId = summary.JobId,
                        JobArn = summary.JobArn,
                        Status = summary.Status?.Value,
                        StatusReason = summary.StatusReason
                    });
                }
            }

            nextToken = response.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return states.Count == 0 ? Array.Empty<AwsBatchJobState>() : states;
    }

    public async Task CancelJobAsync(
        string jobId,
        string reason,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var client = CreateClient(region, serviceUrl);
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
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var client = CreateClient(region, serviceUrl);
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

    // Visible for testing. Applies an opt-in, config-driven ServiceURL override (for example a
    // LocalStack Pro Batch endpoint, or a real-cloud test endpoint) when supplied; when unset the
    // client uses the default regional endpoint, keeping production behaviour unchanged. AWS Batch
    // emulation is a LocalStack Pro feature, so this override is primarily for paid/real coverage.
    internal static AmazonBatchClient CreateClient(string? region, string? serviceUrl, bool submitting = false)
    {
        var config = new AmazonBatchConfig();
        if (submitting)
        {
            config.MaxErrorRetry = 0;
        }

        // An explicit endpoint takes precedence for the request URL while the region (when set)
        // stays the SigV4 signing region. Unset = default regional endpoint.
        AwsClientEndpoint.Apply(config, region, serviceUrl);

        return new AmazonBatchClient(config);
    }
}
