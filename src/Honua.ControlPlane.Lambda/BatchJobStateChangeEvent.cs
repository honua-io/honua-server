// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.ControlPlane.Lambda;

/// <summary>
/// Minimal projection of an Amazon EventBridge "Batch Job State Change" event. Only the fields the
/// reconcile entrypoint needs are modeled; AWS may add properties over time, and System.Text.Json
/// ignores unknown members by default (per the AWS guidance to tolerate unknown properties).
/// <para>
/// The authoritative job selector is <see cref="BatchJobStateChangeDetail.JobId"/> (<c>detail.jobId</c>),
/// which is the AWS Batch provider job id. The control-plane event handler resolves that to the durable
/// operation id and reconciles once — the event payload is never trusted as authoritative state.
/// </para>
/// </summary>
internal sealed class BatchJobStateChangeEvent
{
    /// <summary>Event source. For AWS Batch state-change events this is <c>aws.batch</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Event type. For these events this is <c>Batch Job State Change</c>.</summary>
    [JsonPropertyName("detail-type")]
    public string? DetailType { get; set; }

    /// <summary>The Batch job description carried in the event.</summary>
    [JsonPropertyName("detail")]
    public BatchJobStateChangeDetail? Detail { get; set; }
}

/// <summary>
/// The <c>detail</c> object of a Batch Job State Change event. Carries the provider job id and the
/// reported status (the status is informational only; the reconciler re-reads authoritative state).
/// </summary>
internal sealed class BatchJobStateChangeDetail
{
    /// <summary>The AWS Batch job id — the provider operation id the handler resolves.</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    /// <summary>The AWS Batch job ARN.</summary>
    [JsonPropertyName("jobArn")]
    public string? JobArn { get; set; }

    /// <summary>The job name.</summary>
    [JsonPropertyName("jobName")]
    public string? JobName { get; set; }

    /// <summary>The reported job status (e.g. RUNNING, SUCCEEDED, FAILED). Informational only.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
