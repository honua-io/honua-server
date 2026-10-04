// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Validation;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>
/// Turns a converted gRPC <c>ApplyEdits</c> batch into the batch the shared feature writer
/// applies, enforcing the same edit contract as the other write surfaces (SEC-5): the edit
/// limits, the layer schema (known, typed, editable and required fields), the layer geometry
/// type, attribute rules and contingent values. Updates are merged over the stored row and carry a read-snapshot
/// precondition, so omitted attributes, omitted geometry and fields masked from the caller are
/// kept rather than erased, and a row that changed since the read is not overwritten.
/// </summary>
internal static class GrpcApplyEditsPreparation
{
    /// <summary>
    /// Rejects a batch that exceeds the configured per-kind or per-request edit limits.
    /// </summary>
    /// <param name="batch">The converted batch.</param>
    /// <param name="limits">Configured edit limits.</param>
    public static void EnsureWithinEditLimits(FeatureEditBatch batch, EditLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var adds = batch.Creates.IsDefaultOrEmpty ? 0 : batch.Creates.Length;
        var updates = batch.Updates.IsDefaultOrEmpty ? 0 : batch.Updates.Length;
        var deletes = batch.Deletes.IsDefaultOrEmpty ? 0 : batch.Deletes.Length;
        if (adds > limits.MaxFeaturesPerEdit || updates > limits.MaxFeaturesPerEdit || deletes > limits.MaxFeaturesPerEdit)
        {
            throw InvalidArgument(
                $"Too many features in a single edit operation. Maximum per operation: {limits.MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (adds + updates + deletes > limits.MaxEditsPerTransaction)
        {
            throw InvalidArgument(
                $"Too many edits in a single request. Maximum per request: {limits.MaxEditsPerTransaction.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>
    /// Validates every add and update against the layer and builds the batch to write.
    /// </summary>
    /// <param name="batch">The converted batch.</param>
    /// <param name="resource">Canonical resource of the addressed layer.</param>
    /// <param name="layerGeometryType">Declared geometry type of the addressed layer.</param>
    /// <param name="objectIdFieldName">The layer's object-id field.</param>
    /// <param name="existingRows">Current rows of the update targets, read through the row-security-enforced reader.</param>
    /// <param name="validator">Shared mutation validator.</param>
    /// <param name="unsupportedExpressions">Sink for attribute-rule expressions outside the supported subset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batch to hand to the shared feature writer.</returns>
    public static async Task<FeatureEditBatch> PrepareAsync(
        FeatureEditBatch batch,
        MetadataV2Resource resource,
        MetadataV2GeometryType layerGeometryType,
        string objectIdFieldName,
        IReadOnlyDictionary<long, Feature> existingRows,
        FeatureMutationValidator validator,
        IUnsupportedExpressionSink? unsupportedExpressions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(existingRows);
        ArgumentNullException.ThrowIfNull(validator);

        var creates = batch.Creates;
        if (!creates.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<Feature>(creates.Length);
            for (var i = 0; i < creates.Length; i++)
            {
                builder.Add(await PrepareFeatureAsync(
                    creates[i], existing: null, resource, layerGeometryType, objectIdFieldName, validator,
                    unsupportedExpressions, $"adds[{i.ToString(CultureInfo.InvariantCulture)}]", cancellationToken)
                    .ConfigureAwait(false));
            }

            creates = builder.MoveToImmutable();
        }

        var updates = batch.Updates;
        var preconditions = batch.Preconditions.IsDefault ? ImmutableArray<FeatureEditPrecondition>.Empty : batch.Preconditions;
        if (!updates.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<Feature>(updates.Length);
            var guarded = new HashSet<long>(preconditions.Select(static precondition => precondition.ObjectId));
            var added = ImmutableArray.CreateBuilder<FeatureEditPrecondition>();
            for (var i = 0; i < updates.Length; i++)
            {
                var update = updates[i];
                if (!existingRows.TryGetValue(update.Id, out var existing))
                {
                    // Every update target is pre-read before this point; refuse rather than write blind.
                    throw new RpcException(new Status(
                        StatusCode.NotFound,
                        $"Feature with objectid {update.Id.ToString(CultureInfo.InvariantCulture)} was not found."));
                }

                builder.Add(await PrepareFeatureAsync(
                    update, existing, resource, layerGeometryType, objectIdFieldName, validator,
                    unsupportedExpressions, $"updates[{i.ToString(CultureInfo.InvariantCulture)}]", cancellationToken)
                    .ConfigureAwait(false));

                if (guarded.Add(update.Id))
                {
                    added.Add(new FeatureEditPrecondition
                    {
                        ObjectId = update.Id,
                        ExpectedStateToken = FeatureStateToken.FromReadSnapshot(existing)
                    });
                }
            }

            updates = builder.MoveToImmutable();
            preconditions = preconditions.AddRange(added.ToImmutable());
        }

        return batch with { Creates = creates, Updates = updates, Preconditions = preconditions };
    }

    private static async Task<Feature> PrepareFeatureAsync(
        Feature feature,
        Feature? existing,
        MetadataV2Resource resource,
        MetadataV2GeometryType layerGeometryType,
        string objectIdFieldName,
        FeatureMutationValidator validator,
        IUnsupportedExpressionSink? unsupportedExpressions,
        string location,
        CancellationToken cancellationToken)
    {
        var isUpdate = existing is not null;

        // Internal markers that only the WFS-T parser may set are not client fields here.
        var supplied = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in feature.Attributes)
        {
            if (ValidationExtensions.IsReservedMutationAttribute(name))
            {
                throw InvalidArgument($"{location}: Unknown field '{name}'.");
            }

            // The object id addresses the row (updates) or is assigned by storage (default
            // objectid field); it is never written from the request.
            if (name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) &&
                (isUpdate || objectIdFieldName.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            supplied[name] = value;
        }

        var attributesResult = validator.ValidateAttributes(
            resource,
            supplied,
            ValidationExtensions.AttributeValidationMode.Strict,
            isUpdate);
        if (!attributesResult.IsValid)
        {
            throw InvalidArgument($"{location}: {attributesResult.ErrorMessage ?? "Invalid attributes."}");
        }

        var attributes = existing is { } current
            ? current.Attributes.ToBuilder()
            : ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in attributesResult.Value!)
        {
            attributes[name] = value;
        }

        // An update that sends no geometry keeps the stored one, as it keeps omitted attributes.
        var geometry = feature.Geometry;
        if (geometry is { Length: > 0 })
        {
            if (!IsGeometryTypeCompatible(geometry, layerGeometryType, out var typeError))
            {
                throw InvalidArgument($"{location}: {typeError}");
            }

            var geometryResult = await validator.ValidateGeometryAsync(geometry, cancellationToken).ConfigureAwait(false);
            if (!geometryResult.IsValid)
            {
                throw InvalidArgument($"{location}: Geometry validation failed.");
            }

            geometry = geometryResult.Geometry;
        }
        else if (existing is { } stored)
        {
            geometry = stored.Geometry;
        }

        var editEvent = isUpdate ? AttributeRuleEditEvent.Update : AttributeRuleEditEvent.Insert;
        var ruleResult = AttributeRuleEngine.Apply(resource, attributes.ToImmutable(), editEvent, unsupportedExpressions);
        if (!ruleResult.IsValid)
        {
            throw InvalidArgument($"{location}: {ruleResult.Violations[0].Message}");
        }

        // A create must supply every required field (after calculation rules have run), as on the
        // other create paths.
        if (!isUpdate && EditProcessor.FindMissingRequiredFields(resource, ruleResult.Attributes) is { Count: > 0 } missing)
        {
            throw InvalidArgument($"{location}: Required attribute(s) missing: {string.Join(", ", missing)}.");
        }

        var contingentResult = ContingentValueValidator.Validate(resource, ruleResult.Attributes);
        if (!contingentResult.IsValid)
        {
            throw InvalidArgument($"{location}: {contingentResult.Violations[0].Message}");
        }

        return feature with
        {
            Geometry = geometry,
            Attributes = ruleResult.Attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            PreserveOmittedMaskedAttributes = isUpdate
        };
    }

    /// <summary>
    /// Whether a WKB geometry may be stored on a layer of <paramref name="layerGeometryType"/>.
    /// Single- and multi-part variants of the same family are interchangeable; mixed, collection
    /// and untyped layers accept any geometry.
    /// </summary>
    internal static bool IsGeometryTypeCompatible(byte[] wkb, MetadataV2GeometryType layerGeometryType, out string? error)
    {
        error = null;
        if (layerGeometryType is MetadataV2GeometryType.None
            or MetadataV2GeometryType.Mixed
            or MetadataV2GeometryType.GeometryCollection)
        {
            return true;
        }

        Geometry parsed;
        try
        {
            parsed = new WKBReader().Read(wkb);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or InvalidOperationException)
        {
            error = "Geometry could not be read.";
            return false;
        }

        var layerFamily = Family(layerGeometryType);
        var inputFamily = parsed.OgcGeometryType switch
        {
            OgcGeometryType.Point or OgcGeometryType.MultiPoint => MetadataV2GeometryType.Point,
            OgcGeometryType.LineString or OgcGeometryType.MultiLineString => MetadataV2GeometryType.LineString,
            OgcGeometryType.Polygon or OgcGeometryType.MultiPolygon => MetadataV2GeometryType.Polygon,
            _ => (MetadataV2GeometryType?)null
        };

        if (inputFamily == layerFamily)
        {
            return true;
        }

        error = $"Geometry type {parsed.GeometryType} does not match the layer geometry type {layerGeometryType}.";
        return false;
    }

    private static MetadataV2GeometryType? Family(MetadataV2GeometryType type)
        => type switch
        {
            MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => MetadataV2GeometryType.Point,
            MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => MetadataV2GeometryType.LineString,
            MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => MetadataV2GeometryType.Polygon,
            _ => null
        };

    private static RpcException InvalidArgument(string message)
        => new(new Status(StatusCode.InvalidArgument, message));
}
