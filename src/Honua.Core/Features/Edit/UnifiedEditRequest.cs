// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Unified edit request model that supports all protocol edit features while maintaining clean separation.
/// Acts as a common abstraction for edit semantics across WFS, OGC API Features, GeoServices, and OData.
/// </summary>
public readonly record struct UnifiedEditRequest
{
    /// <summary>
    /// Features to create.
    /// </summary>
    public ImmutableArray<EditFeature>? Creates { get; init; }

    /// <summary>
    /// Features to update with their modifications.
    /// </summary>
    public ImmutableArray<EditFeature>? Updates { get; init; }

    /// <summary>
    /// Features to delete (by object ID).
    /// </summary>
    public ImmutableArray<long>? Deletes { get; init; }

    /// <summary>
    /// Ordered edit operations when sequence matters (alternative to separate create/update/delete arrays).
    /// </summary>
    public ImmutableArray<UnifiedEditOperation>? Operations { get; init; }

    /// <summary>
    /// Transaction options for the edit operation.
    /// </summary>
    public EditTransactionOptions? TransactionOptions { get; init; }

    /// <summary>
    /// Output coordinate reference system for response features.
    /// </summary>
    public EditOutputCrs? OutputCrs { get; init; }

    /// <summary>
    /// Validation options for the edit operation.
    /// </summary>
    public EditValidationOptions? ValidationOptions { get; init; }

    /// <summary>
    /// Protocol-specific extensions that don't fit the unified model.
    /// </summary>
    public ImmutableDictionary<string, object>? Extensions { get; init; }

    /// <summary>
    /// Creates a simple create operation.
    /// </summary>
    /// <param name="features">Features to create</param>
    /// <returns>Unified edit request</returns>
    public static UnifiedEditRequest WithCreates(ImmutableArray<EditFeature> features)
        => new() { Creates = features };

    /// <summary>
    /// Creates a simple update operation.
    /// </summary>
    /// <param name="features">Features to update</param>
    /// <returns>Unified edit request</returns>
    public static UnifiedEditRequest WithUpdates(ImmutableArray<EditFeature> features)
        => new() { Updates = features };

    /// <summary>
    /// Creates a simple delete operation.
    /// </summary>
    /// <param name="objectIds">Object IDs to delete</param>
    /// <returns>Unified edit request</returns>
    public static UnifiedEditRequest WithDeletes(ImmutableArray<long> objectIds)
        => new() { Deletes = objectIds };

    /// <summary>
    /// Creates an edit request with ordered operations.
    /// </summary>
    /// <param name="operations">Ordered edit operations</param>
    /// <returns>Unified edit request</returns>
    public static UnifiedEditRequest WithOperations(ImmutableArray<UnifiedEditOperation> operations)
        => new() { Operations = operations };

    /// <summary>
    /// Checks if this is an empty edit request.
    /// </summary>
    public bool IsEmpty =>
        (Creates?.IsDefaultOrEmpty != false) &&
        (Updates?.IsDefaultOrEmpty != false) &&
        (Deletes?.IsDefaultOrEmpty != false) &&
        (Operations?.IsDefaultOrEmpty != false);

    /// <summary>
    /// Gets the total number of edit operations.
    /// </summary>
    public int TotalOperations
    {
        get
        {
            if (Operations?.IsDefaultOrEmpty == false)
            {
                return Operations.Value.Length;
            }

            return (Creates?.Length ?? 0) + (Updates?.Length ?? 0) + (Deletes?.Length ?? 0);
        }
    }

    /// <summary>
    /// Checks if the request requires transaction semantics.
    /// </summary>
    public bool RequiresTransaction =>
        TotalOperations > 1 ||
        TransactionOptions?.RollbackOnFailure == true ||
        TransactionOptions?.UseExplicitTransaction == true;
}

/// <summary>
/// Represents a feature for editing with protocol-neutral format.
/// </summary>
public readonly record struct EditFeature
{
    /// <summary>
    /// Object ID of the feature (for updates and deletes).
    /// </summary>
    public long? ObjectId { get; init; }

    /// <summary>
    /// Global ID of the feature (if supported by protocol).
    /// </summary>
    public string? GlobalId { get; init; }

    /// <summary>
    /// Feature geometry in WKB format.
    /// </summary>
    public byte[]? Geometry { get; init; }

    /// <summary>
    /// Feature attributes.
    /// </summary>
    public ImmutableDictionary<string, object?>? Attributes { get; init; }

    /// <summary>
    /// Conditional operation constraints (ETags, version numbers, etc.).
    /// </summary>
    public EditConstraints? Constraints { get; init; }

    /// <summary>
    /// Update mode for partial updates.
    /// </summary>
    public EditUpdateMode UpdateMode { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EditFeature"/> struct with default values.
    /// </summary>
    public EditFeature()
    {
        ObjectId = null;
        GlobalId = null;
        Geometry = null;
        Attributes = null;
        Constraints = null;
        UpdateMode = EditUpdateMode.Replace;
        Metadata = null;
    }

    /// <summary>
    /// Protocol-specific metadata for this feature.
    /// </summary>
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates an edit feature for creation.
    /// </summary>
    /// <param name="geometry">Feature geometry</param>
    /// <param name="attributes">Feature attributes</param>
    /// <param name="globalId">Optional global ID</param>
    /// <returns>Edit feature</returns>
    public static EditFeature ForCreate(
        byte[]? geometry,
        ImmutableDictionary<string, object?> attributes,
        string? globalId = null)
        => new()
        {
            Geometry = geometry,
            Attributes = attributes,
            GlobalId = globalId
        };

    /// <summary>
    /// Creates an edit feature for update.
    /// </summary>
    /// <param name="objectId">Object ID</param>
    /// <param name="geometry">Updated geometry</param>
    /// <param name="attributes">Updated attributes</param>
    /// <param name="updateMode">Update mode</param>
    /// <param name="constraints">Optional constraints</param>
    /// <returns>Edit feature</returns>
    public static EditFeature ForUpdate(
        long objectId,
        byte[]? geometry,
        ImmutableDictionary<string, object?>? attributes,
        EditUpdateMode updateMode = EditUpdateMode.Replace,
        EditConstraints? constraints = null)
        => new()
        {
            ObjectId = objectId,
            Geometry = geometry,
            Attributes = attributes,
            UpdateMode = updateMode,
            Constraints = constraints
        };

    /// <summary>
    /// Creates an edit feature for conditional update.
    /// </summary>
    /// <param name="objectId">Object ID</param>
    /// <param name="geometry">Updated geometry</param>
    /// <param name="attributes">Updated attributes</param>
    /// <param name="etag">ETag constraint</param>
    /// <param name="updateMode">Update mode</param>
    /// <returns>Edit feature</returns>
    public static EditFeature ForConditionalUpdate(
        long objectId,
        byte[]? geometry,
        ImmutableDictionary<string, object?>? attributes,
        string etag,
        EditUpdateMode updateMode = EditUpdateMode.Replace)
        => new()
        {
            ObjectId = objectId,
            Geometry = geometry,
            Attributes = attributes,
            UpdateMode = updateMode,
            Constraints = EditConstraints.WithETag(etag)
        };
}

/// <summary>
/// Unified edit operation for ordered execution.
/// </summary>
public readonly record struct UnifiedEditOperation
{
    /// <summary>
    /// Operation type.
    /// </summary>
    public required EditOperationType Type { get; init; }

    /// <summary>
    /// Feature data for create and update operations.
    /// </summary>
    public EditFeature? Feature { get; init; }

    /// <summary>
    /// Object ID for delete operations.
    /// </summary>
    public long? ObjectId { get; init; }

    /// <summary>
    /// Operation-specific metadata.
    /// </summary>
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates a create operation.
    /// </summary>
    /// <param name="feature">Feature to create</param>
    /// <returns>Edit operation</returns>
    public static UnifiedEditOperation Create(EditFeature feature)
        => new() { Type = EditOperationType.Create, Feature = feature };

    /// <summary>
    /// Creates an update operation.
    /// </summary>
    /// <param name="feature">Feature to update</param>
    /// <returns>Edit operation</returns>
    public static UnifiedEditOperation Update(EditFeature feature)
        => new() { Type = EditOperationType.Update, Feature = feature };

    /// <summary>
    /// Creates a delete operation.
    /// </summary>
    /// <param name="objectId">Object ID to delete</param>
    /// <returns>Edit operation</returns>
    public static UnifiedEditOperation Delete(long objectId)
        => new() { Type = EditOperationType.Delete, ObjectId = objectId };
}

/// <summary>
/// Edit operation types.
/// </summary>
public enum EditOperationType
{
    /// <summary>
    /// Create a new feature.
    /// </summary>
    Create,

    /// <summary>
    /// Update an existing feature.
    /// </summary>
    Update,

    /// <summary>
    /// Delete an existing feature.
    /// </summary>
    Delete
}

/// <summary>
/// Update modes for feature modifications.
/// </summary>
public enum EditUpdateMode
{
    /// <summary>
    /// Replace the entire feature (PUT semantics).
    /// </summary>
    Replace,

    /// <summary>
    /// Merge changes with existing feature (PATCH semantics).
    /// </summary>
    Merge,

    /// <summary>
    /// Update only specified attributes.
    /// </summary>
    Partial
}

/// <summary>
/// Transaction options for edit operations.
/// </summary>
public readonly record struct EditTransactionOptions
{
    /// <summary>
    /// Whether to rollback all changes on any failure.
    /// </summary>
    public bool RollbackOnFailure { get; init; }

    /// <summary>
    /// Whether to use explicit transaction handling.
    /// </summary>
    public bool UseExplicitTransaction { get; init; }

    /// <summary>
    /// Transaction isolation level.
    /// </summary>
    public TransactionIsolationLevel IsolationLevel { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EditTransactionOptions"/> struct with default values.
    /// </summary>
    public EditTransactionOptions()
    {
        RollbackOnFailure = false;
        UseExplicitTransaction = false;
        IsolationLevel = TransactionIsolationLevel.ReadCommitted;
        TimeoutMs = null;
        TransactionHandle = null;
        TransactionHints = null;
    }

    /// <summary>
    /// Transaction timeout in milliseconds.
    /// </summary>
    public int? TimeoutMs { get; init; }

    /// <summary>
    /// Transaction handle/ID (for protocols that support it).
    /// </summary>
    public string? TransactionHandle { get; init; }

    /// <summary>
    /// Additional transaction hints.
    /// </summary>
    public ImmutableDictionary<string, object>? TransactionHints { get; init; }

    /// <summary>
    /// Creates transaction options with rollback enabled.
    /// </summary>
    /// <param name="isolationLevel">Isolation level</param>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    /// <returns>Transaction options</returns>
    public static EditTransactionOptions WithRollback(
        TransactionIsolationLevel isolationLevel = TransactionIsolationLevel.ReadCommitted,
        int? timeoutMs = null)
        => new()
        {
            RollbackOnFailure = true,
            UseExplicitTransaction = true,
            IsolationLevel = isolationLevel,
            TimeoutMs = timeoutMs
        };
}

/// <summary>
/// Output coordinate reference system for edit responses.
/// </summary>
public readonly record struct EditOutputCrs
{
    /// <summary>
    /// SRID for output geometry transformation.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Axis order for output coordinates.
    /// </summary>
    public AxisOrder? AxisOrder { get; init; }

    /// <summary>
    /// CRS URI for standards compliance.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// Creates output CRS specification.
    /// </summary>
    /// <param name="srid">SRID</param>
    /// <param name="axisOrder">Axis order</param>
    /// <param name="uri">CRS URI</param>
    /// <returns>Edit output CRS</returns>
    public static EditOutputCrs Create(int srid, AxisOrder? axisOrder = null, string? uri = null)
        => new() { Srid = srid, AxisOrder = axisOrder, Uri = uri };
}

/// <summary>
/// Validation options for edit operations.
/// </summary>
public readonly record struct EditValidationOptions
{
    /// <summary>
    /// Whether to validate geometry.
    /// </summary>
    public bool ValidateGeometry { get; init; }

    /// <summary>
    /// Whether to validate attributes against schema.
    /// </summary>
    public bool ValidateAttributes { get; init; }

    /// <summary>
    /// Whether to validate constraints and references.
    /// </summary>
    public bool ValidateConstraints { get; init; }

    /// <summary>
    /// Whether to validate business rules.
    /// </summary>
    public bool ValidateBusinessRules { get; init; }

    /// <summary>
    /// Validation mode.
    /// </summary>
    public ValidationMode Mode { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="EditValidationOptions"/> struct with strict defaults.
    /// </summary>
    public EditValidationOptions()
    {
        ValidateGeometry = true;
        ValidateAttributes = true;
        ValidateConstraints = true;
        ValidateBusinessRules = true;
        Mode = ValidationMode.Strict;
        CustomRules = null;
    }

    /// <summary>
    /// Custom validation rules.
    /// </summary>
    public ImmutableArray<string>? CustomRules { get; init; }

    /// <summary>
    /// Creates strict validation options.
    /// </summary>
    /// <returns>Strict validation options</returns>
    public static EditValidationOptions Strict()
        => new()
        {
            ValidateGeometry = true,
            ValidateAttributes = true,
            ValidateConstraints = true,
            ValidateBusinessRules = true,
            Mode = ValidationMode.Strict
        };

    /// <summary>
    /// Creates lenient validation options.
    /// </summary>
    /// <returns>Lenient validation options</returns>
    public static EditValidationOptions Lenient()
        => new()
        {
            ValidateGeometry = true,
            ValidateAttributes = true,
            ValidateConstraints = false,
            ValidateBusinessRules = false,
            Mode = ValidationMode.Lenient
        };
}

/// <summary>
/// Validation modes.
/// </summary>
public enum ValidationMode
{
    /// <summary>
    /// Strict validation - all checks must pass.
    /// </summary>
    Strict,

    /// <summary>
    /// Lenient validation - warnings allowed.
    /// </summary>
    Lenient,

    /// <summary>
    /// Skip validation entirely.
    /// </summary>
    Skip
}

/// <summary>
/// Edit constraints for conditional operations.
/// </summary>
public readonly record struct EditConstraints
{
    /// <summary>
    /// ETag value for conditional operations.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Version number for optimistic concurrency.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>
    /// Last modified timestamp.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// Custom constraint values.
    /// </summary>
    public ImmutableDictionary<string, object>? CustomConstraints { get; init; }

    /// <summary>
    /// Creates constraints with ETag.
    /// </summary>
    /// <param name="etag">ETag value</param>
    /// <returns>Edit constraints</returns>
    public static EditConstraints WithETag(string etag)
        => new() { ETag = etag };

    /// <summary>
    /// Creates constraints with version.
    /// </summary>
    /// <param name="version">Version number</param>
    /// <returns>Edit constraints</returns>
    public static EditConstraints WithVersion(long version)
        => new() { Version = version };
}
