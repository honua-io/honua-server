// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Edit;

public sealed record UnifiedEditResult
{
    public required bool IsSuccess { get; init; }

    public FeatureEditResult? Result { get; init; }

    public UnifiedEditRequest? EditRequest { get; init; }

    public EditTransaction? Transaction { get; init; }

    public string? Protocol { get; init; }

    public IDictionary<string, object>? Metadata { get; init; }

    public string? ErrorMessage { get; init; }

    public int? ErrorStatusCode { get; init; }

    public static UnifiedEditResult Success(
        FeatureEditResult result,
        UnifiedEditRequest editRequest,
        EditTransaction transaction,
        string protocol,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            Result = result,
            EditRequest = editRequest,
            Transaction = transaction,
            Protocol = protocol,
            Metadata = metadata
        };

    public static UnifiedEditResult Failure(string errorMessage, int? errorStatusCode = null)
        => new() { IsSuccess = false, ErrorMessage = errorMessage, ErrorStatusCode = errorStatusCode };
}

public sealed record UnifiedBatchEditResult
{
    public required bool IsSuccess { get; init; }

    public ImmutableArray<FeatureEditResult>? Results { get; init; }

    public EditTransaction? Transaction { get; init; }

    public bool AllOperationsSucceeded { get; init; }

    public string? Protocol { get; init; }

    public IDictionary<string, object>? Metadata { get; init; }

    public string? ErrorMessage { get; init; }

    public static UnifiedBatchEditResult Success(
        ImmutableArray<FeatureEditResult> results,
        EditTransaction transaction,
        bool allSucceeded,
        string protocol,
        IDictionary<string, object>? metadata = null)
        => new()
        {
            IsSuccess = true,
            Results = results,
            Transaction = transaction,
            AllOperationsSucceeded = allSucceeded,
            Protocol = protocol,
            Metadata = metadata
        };

    public static UnifiedBatchEditResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage };
}
