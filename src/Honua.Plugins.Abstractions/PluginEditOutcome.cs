// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A per-feature rejection produced by the plugin edit pipeline, tagged with enough information
/// for the host to map it back to the originating protocol response slot.
/// </summary>
/// <param name="Kind">The edit kind of the rejected feature.</param>
/// <param name="RequestIndex">Index of the feature within its kind's request array.</param>
/// <param name="ObjectId">The feature's object id for updates/deletes; <see langword="null"/> for creates.</param>
/// <param name="ErrorCode">Protocol-facing error code.</param>
/// <param name="Message">Safe, client-facing rejection message.</param>
public sealed record PluginEditRejection(
    EditKind Kind,
    int RequestIndex,
    long? ObjectId,
    int ErrorCode,
    string Message);

/// <summary>
/// Aggregate result of running plugin validators and before-edit hooks over an edit batch.
/// </summary>
/// <param name="Rejections">Per-feature rejections; empty when nothing was rejected.</param>
/// <param name="BatchRejected">
/// <see langword="true"/> when a before-edit hook rejected the entire batch (as opposed to
/// individual features). When set, <see cref="Rejections"/> contains one entry per feature.
/// </param>
public sealed record PluginEditOutcome(
    ImmutableArray<PluginEditRejection> Rejections,
    bool BatchRejected)
{
    /// <summary>An outcome with no rejections.</summary>
    public static PluginEditOutcome Allowed { get; } =
        new(ImmutableArray<PluginEditRejection>.Empty, BatchRejected: false);

    /// <summary>Gets whether any feature was rejected.</summary>
    public bool HasRejections => !Rejections.IsDefaultOrEmpty;
}
