// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Query;

public interface IQueryProcessor
{
    QueryValidationResult ValidateQuery(UnifiedQuery query, MetadataV2Object resource);

    UnifiedQuery OptimizeQuery(UnifiedQuery query, MetadataV2Object resource);

    FeatureQuery ToFeatureQuery(UnifiedQuery query, MetadataV2Object resource);

    string BuildCacheKey(UnifiedQuery query, MetadataV2Object resource, string protocol);

    bool ShouldUseStreaming(UnifiedQuery query, MetadataV2Object resource, string outputFormat);

    Task<long> EstimateResultCountAsync(UnifiedQuery query, MetadataV2Object resource, CancellationToken cancellationToken);
}

public sealed record QueryValidationResult
{
    public required bool IsValid { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string>? Warnings { get; init; }

    public static QueryValidationResult Success(IReadOnlyList<string>? warnings = null)
        => new() { IsValid = true, Warnings = warnings };

    public static QueryValidationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}
