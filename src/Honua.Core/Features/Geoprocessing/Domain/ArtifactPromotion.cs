// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Request to promote an artifact from a temporary workspace to a durable destination.
/// </summary>
public sealed record ArtifactPromotionRequest
{
    /// <summary>
    /// Identifier of the artifact to promote.
    /// </summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Identifier of the source workspace containing the artifact.
    /// </summary>
    public required string SourceWorkspaceId { get; init; }

    /// <summary>
    /// Identifier of the target workspace to promote into.
    /// </summary>
    public required string TargetWorkspaceId { get; init; }

    /// <summary>
    /// Optional new label for the promoted artifact.
    /// </summary>
    public string? NewLabel { get; init; }
}

/// <summary>
/// Outcome of an artifact promotion attempt.
/// </summary>
public sealed record ArtifactPromotionResult
{
    /// <summary>
    /// Whether the promotion succeeded.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Identifier of the promoted artifact in the target workspace when successful.
    /// </summary>
    public string? PromotedArtifactId { get; init; }

    /// <summary>
    /// Reason the promotion failed, when applicable.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Creates a successful promotion result.
    /// </summary>
    public static ArtifactPromotionResult Success(string promotedArtifactId) => new()
    {
        Succeeded = true,
        PromotedArtifactId = promotedArtifactId
    };

    /// <summary>
    /// Creates a failed promotion result.
    /// </summary>
    public static ArtifactPromotionResult Failure(string reason) => new()
    {
        Succeeded = false,
        FailureReason = reason
    };
}
