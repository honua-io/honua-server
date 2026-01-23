// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Represents the outcome of a metadata resource write operation.
/// </summary>
public sealed record MetadataResourceWriteResult(
    MetadataResourceWriteOutcome Outcome,
    MetadataResource? Resource,
    string? Error = null)
{
    /// <summary>
    /// Creates a successful write result.
    /// </summary>
    public static MetadataResourceWriteResult Success(MetadataResourceWriteOutcome outcome, MetadataResource resource)
        => new(outcome, resource);

    /// <summary>
    /// Creates a failure write result with an error message.
    /// </summary>
    public static MetadataResourceWriteResult Failure(MetadataResourceWriteOutcome outcome, string error)
        => new(outcome, null, error);
}

/// <summary>
/// Possible outcomes for metadata resource write operations.
/// </summary>
public enum MetadataResourceWriteOutcome
{
    /// <summary>
    /// Resource was created.
    /// </summary>
    Created,

    /// <summary>
    /// Resource was updated.
    /// </summary>
    Updated,

    /// <summary>
    /// Resource was deleted.
    /// </summary>
    Deleted,

    /// <summary>
    /// Resource was not found.
    /// </summary>
    NotFound,

    /// <summary>
    /// Resource write failed due to concurrency conflict.
    /// </summary>
    Conflict,

    /// <summary>
    /// Resource write failed due to validation or storage error.
    /// </summary>
    Failed
}
