// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>Server-owned original workspace/source bounds for an accepted managed process job.</summary>
/// <remarks>This is provenance sealed into the job intent, never a bearer credential or caller-supplied grant.</remarks>
public sealed record AgentManagedProcessJobBinding
{
    /// <summary>Versioned explicit bounded process/input contract.</summary>
    public required string Contract { get; init; }
    /// <summary>Original workspace identity.</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>Original immutable turn identity.</summary>
    public required string TurnId { get; init; }
    /// <summary>SHA256 of the exact original stored turn.</summary>
    public required string TurnHash { get; init; }
    /// <summary>Original actual target identity.</summary>
    public required string TargetId { get; init; }
    /// <summary>Original actual release identity.</summary>
    public required string ReleaseId { get; init; }
    /// <summary>Original canonical workspace owner.</summary>
    public required string WorkspaceOwnerActorId { get; init; }
    /// <summary>Original canonical executor.</summary>
    public required string ExecutorActorId { get; init; }
    /// <summary>Original server-selected tenant.</summary>
    public required string TenantId { get; init; }
    /// <summary>Original policy and credential context digest, excluding the later lease.</summary>
    public required string PolicyContextFingerprint { get; init; }
    /// <summary>Digest of the exact canonical projected worker plan parameters.</summary>
    public required string PlanParametersFingerprint { get; init; }
    /// <summary>Maximum decoded inline source bytes admitted by this contract.</summary>
    public required long MaximumInputBytes { get; init; }
    /// <summary>Maximum inline output bytes; staging is forbidden for this accepted lane.</summary>
    public required long MaximumOutputBytes { get; init; }
    /// <summary>Closed source resource/publication/policy identities selected by the original turn.</summary>
    public required AgentManagedProcessSourceBinding[] Sources { get; init; }
}

/// <summary>One exact original published source and its original row/field policy fingerprint.</summary>
/// <param name="ServiceId">Canonical service metadata identity.</param>
/// <param name="PublishedLayerIndex">Original service-local layer locator.</param>
/// <param name="PublicationId">Canonical publication metadata identity.</param>
/// <param name="ResourceId">Canonical metadata resource identity.</param>
/// <param name="StorageLayerId">Original canonical storage layer identity.</param>
/// <param name="PolicyFingerprint">Original metadata and effective row/field restrictions.</param>
/// <param name="PublicationFingerprint">Original exact service/publication metadata fingerprint.</param>
public sealed record AgentManagedProcessSourceBinding(string ServiceId, int PublishedLayerIndex,
    string PublicationId, string ResourceId, int StorageLayerId, string PolicyFingerprint, string PublicationFingerprint);
