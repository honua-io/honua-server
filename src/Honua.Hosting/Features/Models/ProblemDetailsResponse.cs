// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Validation.Contracts;

namespace Honua.Infrastructure.Models;

internal sealed record ProblemDetailsResponse
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public required string Detail { get; init; }

    public string? Instance { get; init; }

    public string? CorrelationId { get; init; }

    public string? Timestamp { get; init; }

    public IReadOnlyList<FieldValidationError>? Errors { get; init; }

    public string? Code { get; init; }

    public bool? Retryable { get; init; }

    public int? RetryAfterSeconds { get; init; }

    public string? Capability { get; init; }

    public string? MissingDependency { get; init; }

    public string? MissingEntitlement { get; init; }

    public string? Remediation { get; init; }

    public string? RemediationRef { get; init; }

    public string? ConflictingJobId { get; init; }

    public string? PolicyRef { get; init; }

    public string? ProposalId { get; init; }
}
