// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Feature-edit construction and precondition validation for the OData $batch handler.
/// Builds validated edit batches, materializes Feature objects from OData request bodies,
/// and evaluates If-Match/If-None-Match preconditions against existing rows.
/// </summary>
internal sealed partial class ODataBatchHandler
{
    private async Task<(FeatureEditBatch? Batch, string? ErrorMessage)> BuildValidatedEditBatchAsync(
        MetadataV2Resource resource,
        ODataEditRequest request,
        bool rollbackOnFailure,
        CancellationToken cancellationToken)
    {
        var conversion = await _editParameterAdapter.ConvertAsync(request, resource, cancellationToken);
        if (!conversion.IsSuccess || conversion.EditRequest == null || conversion.Transaction == null)
        {
            return (null, conversion.ErrorMessage ?? "Invalid OData edit request.");
        }

        var validation = _editProcessor.ValidateEdit(conversion.EditRequest.Value, resource);
        if (!validation.IsValid)
        {
            return (null, validation.ErrorMessage ?? "Invalid OData edit request.");
        }

        var transactionValidation = _editProcessor.ValidateTransaction(conversion.Transaction.Value, resource);
        if (!transactionValidation.IsValid)
        {
            return (null, transactionValidation.ErrorMessage ?? "Invalid OData edit request.");
        }

        var optimizedRequest = _editProcessor.OptimizeEdit(conversion.EditRequest.Value, resource);
        var editBatch = _editProcessor.ToFeatureEditBatch(optimizedRequest, resource) with
        {
            RollbackOnFailure = rollbackOnFailure
        };

        return (editBatch, null);
    }

    private static ODataEditRequest CreateEditRequestFromFeature(
        ODataOperation operation,
        Feature feature,
        long? objectId = null,
        string? ifMatch = null,
        string? ifNoneMatch = null)
        => new()
        {
            Operation = operation,
            ObjectId = objectId,
            IfMatch = ifMatch,
            IfNoneMatch = ifNoneMatch,
            GeometryWkb = feature.Geometry,
            Payload = new ParsedFeaturePayload
            {
                Attributes = new Dictionary<string, object?>(feature.Attributes, StringComparer.OrdinalIgnoreCase)
            }
        };

    /// <summary>
    /// Returns whether the OData request body explicitly specified a Geometry property.
    /// PATCH and POST both run through CreateFeatureFromBodyAsync, which preserves the
    /// existing row's geometry on partial-update requests; reading the post-merge feature
    /// would report attribute-only PATCH as a geometry change. This helper parses the
    /// raw body once so the outbox queue can be seeded with the request's actual intent.
    /// Parse failures return false because the same body will fail
    /// <c>CreateFeatureFromBodyAsync</c> and the request will not reach the outbox queue.
    /// </summary>
    private static bool TryGetGeometrySpecified(Dictionary<string, object?>? body)
    {
        if (body is null || body.Count == 0)
        {
            return false;
        }

        return ODataFeaturePayloadParser.TryParse(body, out var payload, out _)
            && payload.GeometrySpecified;
    }

    private async Task<Feature> CreateFeatureFromBodyAsync(
        Dictionary<string, object?> body,
        ODataBatchLayerContext layer,
        long? objectId = null,
        Feature? existing = null,
        CancellationToken cancellationToken = default)
    {
        if (!ODataFeaturePayloadParser.TryParse(body, out var payload, out var payloadError))
        {
            throw new ArgumentException(payloadError ?? "Invalid request body.");
        }

        if (payload.LayerId.HasValue && payload.LayerId.Value != layer.PublicLayerId)
        {
            throw new ArgumentException("LayerId in payload does not match request URL.");
        }

        byte[]? geometry = existing.HasValue ? existing.Value.Geometry : null;
        if (payload.GeometrySpecified && payload.Geometry != null)
        {
            var crsResolution = await ODataCrsUtilities.TryResolveGeometryCrsAsync(
                _crsRegistry,
                payload.Geometry,
                layer.Srid,
                cancellationToken);
            if (!crsResolution.IsValid)
            {
                throw new ArgumentException(crsResolution.ErrorMessage ?? "Unsupported geometry CRS.");
            }

            var conversion = ODataGeometryConverter.ConvertGeometryToWkb(
                _geometryService,
                payload.Geometry,
                layer.Srid,
                crsResolution.Definition);

            if (!conversion.IsSuccess)
            {
                throw new ArgumentException(conversion.ErrorMessage ?? "Invalid geometry.");
            }

            geometry = conversion.Wkb;

            var geometryValidation = await _mutationValidator.ValidateGeometryAsync(geometry, cancellationToken);
            if (!geometryValidation.IsValid)
            {
                throw new ArgumentException($"Invalid geometry: {geometryValidation.ErrorMessage}");
            }

            geometry = geometryValidation.Geometry;
        }
        else if (payload.GeometrySpecified)
        {
            geometry = null;
        }

        var attributes = existing.HasValue
            ? new Dictionary<string, object?>(existing.Value.Attributes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (payload.Attributes.Count > 0)
        {
            var attributesResult = _mutationValidator.ValidateAttributes(
                layer.Resource,
                payload.Attributes,
                ValidationExtensions.AttributeValidationMode.Strict,
                isUpdate: existing.HasValue || objectId.HasValue);
            if (!attributesResult.IsValid)
            {
                throw new ArgumentException(attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            foreach (var kvp in attributesResult.Value!)
            {
                attributes[kvp.Key] = kvp.Value;
            }
        }

        return Feature.Create(
            objectId ?? 0,
            geometry,
            attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private async ValueTask<AxisOrder> ResolveAxisOrderAsync(ODataBatchLayerContext layer, CancellationToken cancellationToken)
    {
        if (_axisOrderCache.TryGetValue(layer.PublicLayerId, out var axisOrder))
        {
            return axisOrder;
        }

        axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
            _crsRegistry,
            layer.Srid,
            cancellationToken);
        _axisOrderCache[layer.PublicLayerId] = axisOrder;
        return axisOrder;
    }

    private async Task<(bool IsValid, string? ErrorMessage)> ValidatePreconditionsAsync(
        ODataBatchLayerContext layer,
        Feature feature,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var ifMatch = GetHeaderValue(headers, "If-Match");
        var ifNoneMatch = GetHeaderValue(headers, "If-None-Match");

        if (string.IsNullOrWhiteSpace(ifMatch) && string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return (true, null);
        }

        var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
        var geometry = ODataGeometryConverter.ConvertWkbToGeometry(
            _geometryService,
            feature.Geometry,
            layer.Srid,
            axisOrder);
        var attributes = ODataAttributeSerializer.Serialize(feature.Attributes);
        var payload = ODataUtilityService.BuildFeaturePayload(layer.PublicLayerId, feature, geometry, attributes);
        var etag = ComputeFeatureEtag(payload);

        if (!string.IsNullOrWhiteSpace(ifMatch) &&
            !_etagService.MatchesPrecondition(ifMatch, etag))
        {
            return (false, "ETag does not match the current resource.");
        }

        if (!string.IsNullOrWhiteSpace(ifNoneMatch) &&
            !_etagService.IsModified(ifNoneMatch, etag))
        {
            return (false, "Resource has not changed.");
        }

        return (true, null);
    }

    private static string? GetHeaderValue(Dictionary<string, string>? headers, string name)
    {
        if (headers == null)
        {
            return null;
        }

        foreach (var kvp in headers)
        {
            if (kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }
}
