// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>Applies workload-specific durable resource bounds before a remote retry.</summary>
public interface IExecutionJobRetryGuard
{
    /// <summary>Returns a retry decision, or null when this guard does not own the workload.</summary>
    /// <param name="job">Failed attempt and its retained resource receipts.</param>
    /// <returns>Whether retry is allowed and which receipts must remain charged.</returns>
    ExecutionJobRetryDecision? EvaluateRetry(ExecutionJobRecord job);
}

/// <summary>Workload-specific retry admission without relaxing the canonical attempt cap.</summary>
public sealed record ExecutionJobRetryDecision
{
    /// <summary>Whether another attempt fits the existing resource budget.</summary>
    public required bool Allowed { get; init; }
    /// <summary>Receipts retained and charged across attempts.</summary>
    public IReadOnlyList<string> ArtifactReferences { get; init; } = [];
    /// <summary>Operator-visible bounded reason for refusing another attempt.</summary>
    public string? RefusalReason { get; init; }
}
