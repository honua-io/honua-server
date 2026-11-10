// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.ProcessRelease;

public sealed record GpProcessReleaseRecord
{
    public required GpProcessReleaseLock Lock { get; init; }

    public required string Digest { get; init; }

    public required string PublishedBy { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }
}

public sealed record GpProcessBindingTarget
{
    public required string Kind { get; init; }

    // The worker image is pinned by digest and never by tag.
    public required string Image { get; init; }

    public string? JobDefinitionArn { get; init; }
}

public sealed record GpProcessBinding
{
    public required string Process { get; init; }

    public required int BindingVersion { get; init; }

    public required string LockDigest { get; init; }

    public string? PreviousLockDigest { get; init; }

    public required IReadOnlyList<GpProcessBindingTarget> Targets { get; init; }

    public required string Operation { get; init; }

    public required string ActivatedBy { get; init; }

    public required DateTimeOffset ActivatedAt { get; init; }
}

public sealed record GpProcessBindingActivation
{
    public required string Process { get; init; }

    public required string LockDigest { get; init; }

    public required int ExpectedBindingVersion { get; init; }

    public required string Operation { get; init; }

    public required string Actor { get; init; }

    public required IReadOnlyList<GpProcessBindingTargetIdentity> Targets { get; init; }
}

public sealed record GpProcessBindingTargetIdentity
{
    public required string Kind { get; init; }

    public string? JobDefinitionArn { get; init; }
}

public sealed record GpProcessReleaseEnvironment
{
    public required int ServerContractVersion { get; init; }

    public required IReadOnlyList<string> SupportedTargetKinds { get; init; }

    public IReadOnlyList<GpProcessBindingTarget> ReadyTargets { get; init; } = [];

    public required bool CustomCodeMayUseKubernetes { get; init; }
}

public sealed record GpProcessBindingDecision
{
    public required bool Activated { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public required IReadOnlyList<string> Details { get; init; }

    public GpProcessBinding? Binding { get; init; }
}

public static class GpProcessBindingRefusal
{
    public const string LockNotFound = "LOCK_NOT_FOUND";

    public const string ProcessMismatch = "PROCESS_MISMATCH";

    public const string StaleBindingVersion = "STALE_BINDING_VERSION";

    public const string IncompatibleServerContract = "INCOMPATIBLE_SERVER_CONTRACT";

    public const string TargetUnsupported = "TARGET_UNSUPPORTED";

    public const string TargetIdentityMissing = "TARGET_IDENTITY_MISSING";

    public const string TargetNotReady = "TARGET_NOT_READY";

    public const string RollbackTargetMissing = "ROLLBACK_TARGET_MISSING";

    public const string RollbackTargetMismatch = "ROLLBACK_TARGET_MISMATCH";

    public const string OperationInvalid = "OPERATION_INVALID";
}
