// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.ProcessRelease;

/// <summary>A release lock the server holds as an immutable record (honua-server#6221).</summary>
public sealed record GpProcessReleaseRecord
{
    /// <summary>The lock exactly as published.</summary>
    public required GpProcessReleaseLock Lock { get; init; }

    /// <summary>The lock's canonical digest; the record's identity.</summary>
    public required string Digest { get; init; }

    /// <summary>Who published it (principal name or workflow actor).</summary>
    public required string PublishedBy { get; init; }

    /// <summary>When it was first published; a re-publish of the same digest keeps this.</summary>
    public required DateTimeOffset PublishedAt { get; init; }
}

/// <summary>The provider identity a binding pins for one execution target kind.</summary>
public sealed record GpProcessBindingTarget
{
    /// <summary><c>kubernetes-job</c> or <c>aws-batch</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The image reference jobs on this target run, pinned by digest
    /// (<c>registry/repository@sha256:…</c>). Taken from the lock's target worker digest and
    /// the runtime image reference, never from a tag.
    /// </summary>
    public required string Image { get; init; }

    /// <summary>
    /// AWS Batch only: the job definition ARN with its revision (<c>…:job-definition/name:12</c>)
    /// that the deployment registered for this release. Jobs admitted under the binding are
    /// submitted to exactly this revision.
    /// </summary>
    public string? JobDefinitionArn { get; init; }
}

/// <summary>
/// The versioned, atomic mapping from a logical process name to the release it executes.
/// </summary>
public sealed record GpProcessBinding
{
    /// <summary>Logical process name.</summary>
    public required string Process { get; init; }

    /// <summary>Monotonic version; every activation increments it. Zero means never bound.</summary>
    public required int BindingVersion { get; init; }

    /// <summary>The active release's lock digest.</summary>
    public required string LockDigest { get; init; }

    /// <summary>The release that was active before this activation; the rollback target.</summary>
    public string? PreviousLockDigest { get; init; }

    /// <summary>Pinned provider identities per target kind.</summary>
    public required IReadOnlyList<GpProcessBindingTarget> Targets { get; init; }

    /// <summary><c>deploy</c> or <c>rollback</c>.</summary>
    public required string Operation { get; init; }

    /// <summary>Who activated it.</summary>
    public required string ActivatedBy { get; init; }

    /// <summary>When it was activated.</summary>
    public required DateTimeOffset ActivatedAt { get; init; }
}

/// <summary>A request to make a published release the active one for a process.</summary>
public sealed record GpProcessBindingActivation
{
    /// <summary>Logical process name; must equal the lock's process name.</summary>
    public required string Process { get; init; }

    /// <summary>Digest of a published lock.</summary>
    public required string LockDigest { get; init; }

    /// <summary>
    /// The binding version the caller observed. Activation is refused when the live version
    /// differs: that is a stale concurrent attempt. Zero for a first activation.
    /// </summary>
    public required int ExpectedBindingVersion { get; init; }

    /// <summary><c>deploy</c>, or <c>rollback</c> (then the lock must be the binding's previous release).</summary>
    public required string Operation { get; init; }

    /// <summary>Who is activating.</summary>
    public required string Actor { get; init; }

    /// <summary>
    /// Provider identities the deployment registered, keyed by target kind. Required for every
    /// target kind the lock declares that the environment supports; AWS Batch needs the job
    /// definition revision ARN.
    /// </summary>
    public required IReadOnlyList<GpProcessBindingTargetIdentity> Targets { get; init; }
}

/// <summary>Provider identity supplied with an activation for one target kind.</summary>
public sealed record GpProcessBindingTargetIdentity
{
    /// <summary><c>kubernetes-job</c> or <c>aws-batch</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>AWS Batch job definition ARN with revision.</summary>
    public string? JobDefinitionArn { get; init; }
}

/// <summary>What this server can execute; the facts an activation is checked against.</summary>
public sealed record GpProcessReleaseEnvironment
{
    /// <summary>The serving↔worker execution contract version this server runs.</summary>
    public required int ServerContractVersion { get; init; }

    /// <summary>Target kinds with an activatable execution workload on this server.</summary>
    public required IReadOnlyList<string> SupportedTargetKinds { get; init; }

    /// <summary>
    /// Whether custom-code processes may bind a Kubernetes Job target. ADR-0063 makes custom
    /// code AWS-Batch-only, so this is false unless an operator policy says otherwise.
    /// </summary>
    public required bool CustomCodeMayUseKubernetes { get; init; }
}

/// <summary>The outcome of an activation attempt.</summary>
public sealed record GpProcessBindingDecision
{
    /// <summary>True when the binding changed.</summary>
    public required bool Activated { get; init; }

    /// <summary>Machine-readable refusal codes; empty when activated.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>Human-readable detail per reason.</summary>
    public required IReadOnlyList<string> Details { get; init; }

    /// <summary>The binding after the call: the new one when activated, the live one when refused.</summary>
    public GpProcessBinding? Binding { get; init; }
}

/// <summary>Refusal codes shared with honua-release's <c>gp_process_release.py preflight</c>.</summary>
public static class GpProcessBindingRefusal
{
    /// <summary>No published lock has the requested digest.</summary>
    public const string LockNotFound = "LOCK_NOT_FOUND";

    /// <summary>The lock belongs to another process.</summary>
    public const string ProcessMismatch = "PROCESS_MISMATCH";

    /// <summary>The live binding version is not the one the caller observed.</summary>
    public const string StaleBindingVersion = "STALE_BINDING_VERSION";

    /// <summary>The server contract version is outside the lock's compatibility window.</summary>
    public const string IncompatibleServerContract = "INCOMPATIBLE_SERVER_CONTRACT";

    /// <summary>The lock declares a target this server cannot execute.</summary>
    public const string TargetUnsupported = "TARGET_UNSUPPORTED";

    /// <summary>A supported target lacks the provider identity the activation must pin.</summary>
    public const string TargetIdentityMissing = "TARGET_IDENTITY_MISSING";

    /// <summary>Rollback requested on a process with no previous release.</summary>
    public const string RollbackTargetMissing = "ROLLBACK_TARGET_MISSING";

    /// <summary>Rollback requested to a lock that is not the binding's previous release.</summary>
    public const string RollbackTargetMismatch = "ROLLBACK_TARGET_MISMATCH";

    /// <summary>The operation is neither deploy nor rollback.</summary>
    public const string OperationInvalid = "OPERATION_INVALID";
}
