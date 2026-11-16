// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record ArtifactPromotionRequest
{
    public required string ArtifactId { get; init; }

    public required string SourceWorkspaceId { get; init; }

    public required string TargetWorkspaceId { get; init; }

    public string? NewLabel { get; init; }
}

public sealed record ArtifactPromotionResult
{
    public required bool Succeeded { get; init; }

    public string? PromotedArtifactId { get; init; }

    public string? FailureReason { get; init; }

    public static ArtifactPromotionResult Success(string promotedArtifactId) => new()
    {
        Succeeded = true,
        PromotedArtifactId = promotedArtifactId
    };

    public static ArtifactPromotionResult Failure(string reason) => new()
    {
        Succeeded = false,
        FailureReason = reason
    };
}
