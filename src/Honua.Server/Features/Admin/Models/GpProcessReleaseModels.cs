// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>A published GP process release (honua-server#6221).</summary>
public sealed class GpProcessReleaseResponse
{
    /// <summary>Canonical lock digest, the release identity.</summary>
    [JsonPropertyName("lockDigest")]
    public string LockDigest { get; init; } = string.Empty;

    /// <summary>Logical process name.</summary>
    [JsonPropertyName("process")]
    public string Process { get; init; } = string.Empty;

    /// <summary>Semantic version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>Source commit.</summary>
    [JsonPropertyName("sourceSha")]
    public string SourceSha { get; init; } = string.Empty;

    /// <summary>Who published it.</summary>
    [JsonPropertyName("publishedBy")]
    public string PublishedBy { get; init; } = string.Empty;

    /// <summary>When it was first published.</summary>
    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; init; }

    /// <summary>True when this request created the record; false when the digest already existed.</summary>
    [JsonPropertyName("created")]
    public bool? Created { get; init; }
}

/// <summary>A list of published releases.</summary>
public sealed class GpProcessReleaseListResponse
{
    /// <summary>Releases, newest first.</summary>
    [JsonPropertyName("releases")]
    public IReadOnlyList<GpProcessReleaseResponse> Releases { get; init; } = [];
}

/// <summary>A pinned provider identity for one target kind.</summary>
public sealed class GpProcessBindingTargetResponse
{
    /// <summary><c>kubernetes-job</c> or <c>aws-batch</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Image reference pinned by digest.</summary>
    [JsonPropertyName("image")]
    public string Image { get; init; } = string.Empty;

    /// <summary>AWS Batch job definition ARN with revision.</summary>
    [JsonPropertyName("jobDefinitionArn")]
    public string? JobDefinitionArn { get; init; }
}

/// <summary>
/// The live binding of a process plus what this server can execute: the fields the deploy
/// workflow needs before it changes the binding (<c>gp-environment-state.v1</c>).
/// </summary>
public sealed class GpProcessBindingResponse
{
    /// <summary>Logical process name.</summary>
    [JsonPropertyName("process")]
    public string Process { get; init; } = string.Empty;

    /// <summary>Live binding version; zero when the process was never bound.</summary>
    [JsonPropertyName("bindingVersion")]
    public int BindingVersion { get; init; }

    /// <summary>Active lock digest, null when never bound.</summary>
    [JsonPropertyName("lockDigest")]
    public string? LockDigest { get; init; }

    /// <summary>The rollback target, when any.</summary>
    [JsonPropertyName("previousLockDigest")]
    public string? PreviousLockDigest { get; init; }

    /// <summary>Pinned provider identities.</summary>
    [JsonPropertyName("targets")]
    public IReadOnlyList<GpProcessBindingTargetResponse> Targets { get; init; } = [];

    /// <summary><c>deploy</c> or <c>rollback</c> for the live binding.</summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    /// <summary>Who activated the live binding.</summary>
    [JsonPropertyName("activatedBy")]
    public string? ActivatedBy { get; init; }

    /// <summary>When the live binding was activated.</summary>
    [JsonPropertyName("activatedAt")]
    public DateTimeOffset? ActivatedAt { get; init; }

    /// <summary>The execution contract version this server runs.</summary>
    [JsonPropertyName("serverContractVersion")]
    public int ServerContractVersion { get; init; }

    /// <summary>Target kinds with an activatable execution workload here.</summary>
    [JsonPropertyName("supportedTargets")]
    public IReadOnlyList<string> SupportedTargets { get; init; } = [];

    /// <summary>
    /// How schedules relate to the binding: scheduled workflow runs are admitted like any
    /// submission, so a binding change applies to their next fire and never copies or
    /// duplicates a schedule.
    /// </summary>
    [JsonPropertyName("scheduleReconciliation")]
    public string ScheduleReconciliation { get; init; } = string.Empty;
}

/// <summary>Every live binding.</summary>
public sealed class GpProcessBindingListResponse
{
    /// <summary>Bindings by process name.</summary>
    [JsonPropertyName("bindings")]
    public IReadOnlyList<GpProcessBindingResponse> Bindings { get; init; } = [];
}

/// <summary>Provider identity supplied with an activation.</summary>
public sealed class GpProcessBindingTargetRequest
{
    /// <summary><c>kubernetes-job</c> or <c>aws-batch</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>AWS Batch job definition ARN with revision.</summary>
    [JsonPropertyName("jobDefinitionArn")]
    public string? JobDefinitionArn { get; init; }
}

/// <summary>Activate a published release for a process.</summary>
public sealed class GpProcessBindingActivateRequest
{
    /// <summary>Digest of a published lock.</summary>
    [JsonPropertyName("lockDigest")]
    public string LockDigest { get; init; } = string.Empty;

    /// <summary>The binding version the caller observed; zero for a first activation.</summary>
    [JsonPropertyName("expectedBindingVersion")]
    public int ExpectedBindingVersion { get; init; }

    /// <summary><c>deploy</c> (default) or <c>rollback</c>.</summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    /// <summary>Provider identities per target kind.</summary>
    [JsonPropertyName("targets")]
    public IReadOnlyList<GpProcessBindingTargetRequest> Targets { get; init; } = [];

}

/// <summary>A refused activation, returned with HTTP 409.</summary>
public sealed class GpProcessBindingRefusedResponse
{
    /// <summary>Machine-readable refusal codes.</summary>
    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>One explanation per reason.</summary>
    [JsonPropertyName("details")]
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>The live binding the request was evaluated against.</summary>
    [JsonPropertyName("binding")]
    public GpProcessBindingResponse? Binding { get; init; }
}
