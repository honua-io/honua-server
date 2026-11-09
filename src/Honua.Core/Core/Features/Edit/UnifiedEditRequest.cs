// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Edit;

public readonly record struct UnifiedEditRequest
{
    public ImmutableArray<EditFeature>? Creates { get; init; }

    public ImmutableArray<EditFeature>? Updates { get; init; }

    public ImmutableArray<long>? Deletes { get; init; }

    public ImmutableArray<UnifiedEditOperation>? Operations { get; init; }

    public EditTransactionOptions? TransactionOptions { get; init; }

    public EditOutputCrs? OutputCrs { get; init; }

    public EditValidationOptions? ValidationOptions { get; init; }

    public ImmutableDictionary<string, object>? Extensions { get; init; }

    public ImmutableArray<FeatureEditPrecondition>? Preconditions { get; init; }

    public static UnifiedEditRequest WithCreates(ImmutableArray<EditFeature> features)
        => new() { Creates = features };

    public static UnifiedEditRequest WithUpdates(ImmutableArray<EditFeature> features)
        => new() { Updates = features };

    public static UnifiedEditRequest WithDeletes(ImmutableArray<long> objectIds)
        => new() { Deletes = objectIds };

    public static UnifiedEditRequest WithOperations(ImmutableArray<UnifiedEditOperation> operations)
        => new() { Operations = operations };

    public bool IsEmpty =>
        (Creates?.IsDefaultOrEmpty != false) &&
        (Updates?.IsDefaultOrEmpty != false) &&
        (Deletes?.IsDefaultOrEmpty != false) &&
        (Operations?.IsDefaultOrEmpty != false);

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

    public bool RequiresTransaction =>
        TotalOperations > 1 ||
        TransactionOptions?.RollbackOnFailure == true ||
        TransactionOptions?.UseExplicitTransaction == true;
}

public readonly record struct EditFeature
{
    public long? ObjectId { get; init; }

    public string? GlobalId { get; init; }

    public byte[]? Geometry { get; init; }

    public ImmutableDictionary<string, object?>? Attributes { get; init; }

    public EditConstraints? Constraints { get; init; }

    public EditUpdateMode UpdateMode { get; init; }

    [JsonIgnore]
    public bool PreserveOmittedMaskedAttributes { get; init; }

    // Attribute names a partial update removed must not be restored from the masked read snapshot.
    [JsonIgnore]
    public ImmutableArray<string> ExplicitAttributeRemovals { get; init; }

    [JsonIgnore]
    public bool PreserveOmittedGeometry { get; init; }

    public EditFeature()
    {
        ObjectId = null;
        GlobalId = null;
        Geometry = null;
        Attributes = null;
        Constraints = null;
        UpdateMode = EditUpdateMode.Replace;
        PreserveOmittedMaskedAttributes = false;
        ExplicitAttributeRemovals = ImmutableArray<string>.Empty;
        PreserveOmittedGeometry = false;
        Metadata = null;
    }

    public ImmutableDictionary<string, object>? Metadata { get; init; }

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

public readonly record struct UnifiedEditOperation
{
    public required EditOperationType Type { get; init; }

    public EditFeature? Feature { get; init; }

    public long? ObjectId { get; init; }

    public ImmutableDictionary<string, object>? Metadata { get; init; }

    public static UnifiedEditOperation Create(EditFeature feature)
        => new() { Type = EditOperationType.Create, Feature = feature };

    public static UnifiedEditOperation Update(EditFeature feature)
        => new() { Type = EditOperationType.Update, Feature = feature };

    public static UnifiedEditOperation Delete(long objectId)
        => new() { Type = EditOperationType.Delete, ObjectId = objectId };
}

public enum EditOperationType
{
    Create,

    Update,

    Delete
}

public enum EditUpdateMode
{
    Replace,

    Merge,

    Partial
}

public readonly record struct EditTransactionOptions
{
    public bool RollbackOnFailure { get; init; }

    public bool UseExplicitTransaction { get; init; }

    public TransactionIsolationLevel IsolationLevel { get; init; }

    public EditTransactionOptions()
    {
        RollbackOnFailure = false;
        UseExplicitTransaction = false;
        IsolationLevel = TransactionIsolationLevel.ReadCommitted;
        TimeoutMs = null;
        TransactionHandle = null;
        TransactionHints = null;
    }

    public int? TimeoutMs { get; init; }

    public string? TransactionHandle { get; init; }

    public ImmutableDictionary<string, object>? TransactionHints { get; init; }

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

public readonly record struct EditOutputCrs
{
    public int? Srid { get; init; }

    public AxisOrder? AxisOrder { get; init; }

    public string? Uri { get; init; }

    public static EditOutputCrs Create(int srid, AxisOrder? axisOrder = null, string? uri = null)
        => new() { Srid = srid, AxisOrder = axisOrder, Uri = uri };
}

public readonly record struct EditValidationOptions
{
    public bool ValidateGeometry { get; init; }

    public bool ValidateAttributes { get; init; }

    public bool ValidateConstraints { get; init; }

    public bool ValidateBusinessRules { get; init; }

    public ValidationMode Mode { get; init; }

    public EditValidationOptions()
    {
        ValidateGeometry = true;
        ValidateAttributes = true;
        ValidateConstraints = true;
        ValidateBusinessRules = true;
        Mode = ValidationMode.Strict;
        CustomRules = null;
    }

    public ImmutableArray<string>? CustomRules { get; init; }

    public static EditValidationOptions Strict()
        => new()
        {
            ValidateGeometry = true,
            ValidateAttributes = true,
            ValidateConstraints = true,
            ValidateBusinessRules = true,
            Mode = ValidationMode.Strict
        };

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

public enum ValidationMode
{
    Strict,

    Lenient,

    Skip
}

public readonly record struct EditConstraints
{
    public string? ETag { get; init; }

    public long? Version { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public ImmutableDictionary<string, object>? CustomConstraints { get; init; }

    public string? ExpectedStateToken { get; init; }

    public static EditConstraints WithETag(string etag)
        => new() { ETag = etag };

    public static EditConstraints WithExpectedState(string expectedStateToken, string? etag = null)
        => new() { ExpectedStateToken = expectedStateToken, ETag = etag };

    public static EditConstraints WithVersion(long version)
        => new() { Version = version };
}
