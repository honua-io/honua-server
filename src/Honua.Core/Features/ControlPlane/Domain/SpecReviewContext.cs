// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>Persisted GA layer publication SpecLayerIntent contract; contains no credentials.</summary>
public sealed record SpecLayerIntent
{
    /// <summary>BundleVersion binding.</summary>
    public string BundleVersion { get; init; } = string.Empty;

    /// <summary>Canonical workspace identity.</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>Canonical view identity.</summary>
    public string ViewId { get; init; } = string.Empty;

    /// <summary>Server target identity.</summary>
    public string TargetId { get; init; } = string.Empty;

    /// <summary>Environment identity.</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>ContextRevision binding.</summary>
    public string ContextRevision { get; init; } = string.Empty;

    /// <summary>ConnectionId binding.</summary>
    public Guid ConnectionId { get; init; }

    /// <summary>Schema binding.</summary>
    public string Schema { get; init; } = string.Empty;

    /// <summary>Table binding.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>LayerName binding.</summary>
    public string LayerName { get; init; } = string.Empty;

    /// <summary>ServiceName binding.</summary>
    public string ServiceName { get; init; } = "default";

    /// <summary>ExpectEnabled binding.</summary>
    public bool? ExpectEnabled { get; init; }

}

/// <summary>Persisted GA layer publication SpecReviewContext contract; contains no credentials.</summary>
public sealed record SpecReviewContext
{
    /// <summary>Spec binding.</summary>
    public SpecLayerIntent Spec { get; init; } = new();

    /// <summary>Revision binding.</summary>
    public string Revision { get; init; } = string.Empty;

    /// <summary>ActionHash binding.</summary>
    public string ActionHash { get; init; } = string.Empty;

    /// <summary>VerificationHash binding.</summary>
    public string VerificationHash { get; init; } = string.Empty;

    /// <summary>Hash of the authorized canonical workspace snapshot.</summary>
    public string ContextHash { get; init; } = string.Empty;

    /// <summary>ObservedHash binding.</summary>
    public string ObservedHash { get; init; } = string.Empty;

    /// <summary>Hash of the independent catalog readback after Apply, when observed.</summary>
    public string? ReadbackHash { get; init; }

    /// <summary>PolicyRevision binding.</summary>
    public string PolicyRevision { get; init; } = string.Empty;

    /// <summary>Verdict binding.</summary>
    public string Verdict { get; init; } = "refuse";

    /// <summary>Evidence binding.</summary>
    public string Evidence { get; init; } = "unverified";

    /// <summary>DraftedBy binding.</summary>
    public string DraftedBy { get; init; } = string.Empty;

    /// <summary>AppliedBy binding.</summary>
    public string? AppliedBy { get; init; }

    /// <summary>CommandId binding.</summary>
    public string? CommandId { get; init; }

    /// <summary>LayerId binding.</summary>
    public int? LayerId { get; init; }

}
