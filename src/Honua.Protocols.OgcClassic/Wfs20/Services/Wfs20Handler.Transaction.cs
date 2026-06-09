// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.ServiceDefaults;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Core handler for WFS 2.0 operations backed by the shared catalog and feature stores.
/// </summary>
internal sealed partial class Wfs20Handler
{
    public async Task<IResult> HandleTransactionAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.transaction", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, Wfs20Utilities.Operations.Transaction);

        try
        {
            var document = await ReadTransactionDocumentAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, Wfs20Utilities.Operations.Transaction, StringComparison.OrdinalIgnoreCase))
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "OperationParsingFailed",
                    "Transaction payload must contain a wfs:Transaction root element.",
                    "request");
            }

            var rollbackOnFailure = ResolveRollbackOnFailure(context.Request, root);
            var prepared = await PrepareTransactionAsync(
                context,
                root,
                cancellationToken).ConfigureAwait(false);

            if (!prepared.IsValid)
            {
                return prepared.ErrorResult!;
            }

            if (prepared.Operations.IsDefaultOrEmpty)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "MissingParameterValue",
                    "Transaction request did not contain any supported operations.",
                    "request");
            }

            Wfs20Log.TransactionRequested(_logger, prepared.InsertCount, prepared.UpdateCount, prepared.DeleteCount);

            var editResult = await ApplyPreparedTransactionAsync(
                context,
                prepared,
                rollbackOnFailure,
                cancellationToken).ConfigureAwait(false);

            var committedChangeCount = editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount;
            if (committedChangeCount > 0 && !editResult.WasRolledBack)
            {
                var serviceIdsByLayer = await ResolveTransactionServiceIdsAsync(
                    context,
                    prepared,
                    cancellationToken).ConfigureAwait(false);

                foreach (var (layerId, serviceId) in serviceIdsByLayer)
                {
                    await _mutationEventService.InvalidateLayerAsync(serviceId, layerId, CancellationToken.None).ConfigureAwait(false);
                }

                await PublishTransactionEventsAsync(
                    context,
                    prepared,
                    editResult,
                    serviceIdsByLayer,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (editResult.HasErrors)
            {
                var firstError = GetFirstTransactionError(editResult);
                if (editResult.WasRolledBack)
                {
                    Wfs20Log.TransactionRolledBack(_logger, firstError);
                }

                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "OperationProcessingFailed",
                    firstError,
                    "Transaction");
            }

            Wfs20Log.TransactionReturned(_logger, editResult.CreatedCount, editResult.UpdatedCount, editResult.DeletedCount);
            HonuaTelemetry.SetSuccess(activity, committedChangeCount);

            var responseXml = BuildTransactionResponseXml(prepared, editResult);
            return Results.Content(responseXml, "application/xml", Encoding.UTF8);
        }
        catch (InvalidDataException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            HonuaTelemetry.RecordException(activity, ex);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "OperationParsingFailed",
                ex.Message,
                "request");
        }
        catch (WfsTransactionException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            HonuaTelemetry.RecordException(activity, ex);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                ex.ExceptionCode,
                ex.Message,
                ex.Locator);
        }
        catch (ArgumentException ex)
        {
            Wfs20Log.ParameterValidationFailed(_logger, ex.Message);
            HonuaTelemetry.RecordException(activity, ex);
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                ex.Message,
                "request");
        }
        catch (NotSupportedException ex)
        {
            Wfs20Log.UnsupportedOperationRequested(_logger, ex.Message);
            HonuaTelemetry.RecordException(activity, ex);
            return Wfs20ErrorResults.CreateNotImplemented(
                context,
                "OperationNotSupported",
                ex.Message,
                "request");
        }
        catch (RequestBodyTooLargeException)
        {
            return StandardErrorHelpers.CreatePayloadTooLarge(context, "Request payload is too large.");
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(_logger, Wfs20Utilities.Operations.Transaction, ex.Message);
            HonuaTelemetry.RecordException(activity, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "Failed to process Transaction request.");
        }
    }


    private async Task<TransactionPreparationResult> PrepareTransactionAsync(
        HttpContext context,
        XElement root,
        CancellationToken cancellationToken)
    {
        var descriptors = await GetPublishedFeatureTypesAsync(context, cancellationToken).ConfigureAwait(false);
        var operations = ImmutableArray.CreateBuilder<PreparedTransactionOperation>();
        var distinctLayerIds = new HashSet<int>();
        var validatedLayerIds = new HashSet<int>();

        foreach (var actionElement in root.Elements())
        {
            IResult? errorResult = actionElement.Name.LocalName switch
            {
                "Insert" => await PrepareInsertOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Update" => await PrepareUpdateOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Delete" => await PrepareDeleteOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Replace" => await PrepareReplaceOperationsAsync(
                    context,
                    actionElement,
                    descriptors,
                    operations,
                    distinctLayerIds,
                    validatedLayerIds,
                    cancellationToken).ConfigureAwait(false),
                "Native" => Wfs20ErrorResults.CreateNotImplemented(
                    context,
                    "OperationNotSupported",
                    "Native transaction actions are not supported.",
                    "request"),
                _ => Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Unsupported transaction action '{actionElement.Name.LocalName}'.",
                    "request")
            };

            if (errorResult != null)
            {
                return TransactionPreparationResult.Failure(errorResult);
            }
        }

        if (operations.Count > _editLimits.MaxEditsPerTransaction)
        {
            return TransactionPreparationResult.Failure(Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Transaction contains {operations.Count.ToString(CultureInfo.InvariantCulture)} operations, exceeding the maximum of {_editLimits.MaxEditsPerTransaction.ToString(CultureInfo.InvariantCulture)}.",
                "request"));
        }

        // A single wfs:Transaction may carry actions for several feature types; the WFS 2.0 ETS
        // batch-replaces and batch-updates features across every advertised type in one request
        // (org.opengis.cite.iso19142.transaction.ReplaceTests / Update). ApplyPreparedTransactionAsync
        // groups operations by storage layer and applies each group in its own data-layer
        // transaction, so each feature type is committed atomically. The underlying feature writer
        // scopes its transaction to a single layer, so honua does not provide a single cross-layer
        // atomic rollback when actions span multiple types; per-layer failures are still surfaced as
        // a transaction error. This matches the typical WFS-T server backed by per-table edits and is
        // sufficient for the certified basic profile, so multi-feature-type transactions are accepted
        // rather than rejected with OperationNotSupported.
        var layerId = operations.Count == 0
            ? 0
            : operations[0].Descriptor.StorageLayerId;

        return TransactionPreparationResult.Success(
            operations.ToImmutable(),
            layerId,
            distinctLayerIds.Count);
    }


    private async Task<IResult?> PrepareInsertOperationsAsync(
        HttpContext context,
        XElement insertElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = insertElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var featureElements = insertElement.Elements().ToArray();
        if (featureElements.Length == 0)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Insert action must contain at least one feature element.",
                "Insert");
        }

        if (featureElements.Length > _editLimits.MaxFeaturesPerEdit)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Insert action exceeds the maximum of {_editLimits.MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture)} features.",
                "Insert");
        }

        foreach (var featureElement in featureElements)
        {
            var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, featureElement.Name.LocalName);
            var validationError = await ValidateTransactionLayerWriteAccessAsync(
                context,
                descriptor.StorageLayerId,
                validatedLayerIds,
                cancellationToken).ConfigureAwait(false);
            if (validationError != null)
            {
                return validationError;
            }

            distinctLayerIds.Add(descriptor.StorageLayerId);
            var feature = await BuildTransactionInsertFeatureAsync(
                descriptor.Resource,
                featureElement,
                cancellationToken).ConfigureAwait(false);
            // Insert always carries the request's geometry intent in the built feature.
            operations.Add(new PreparedTransactionOperation(
                operations.Count,
                descriptor,
                TransactionActionKind.Insert,
                FeatureEditOperation.Create(feature),
                handle,
                feature,
                null)
            {
                RequestGeometryChanged = feature.Geometry is { Length: > 0 }
            });
        }

        return null;
    }


    private async Task<IResult?> PrepareUpdateOperationsAsync(
        HttpContext context,
        XElement updateElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = updateElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var typeName = updateElement.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "typeName", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attribute.Name.LocalName, "typeNames", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Update action requires a typeName attribute.",
                "typeName");
        }

        var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, typeName);
        var validationError = await ValidateTransactionLayerWriteAccessAsync(
            context,
            descriptor.StorageLayerId,
            validatedLayerIds,
            cancellationToken).ConfigureAwait(false);
        if (validationError != null)
        {
            return validationError;
        }

        distinctLayerIds.Add(descriptor.StorageLayerId);

        var changes = ParseTransactionUpdateChanges(descriptor.Resource, updateElement);
        var targetIds = await ResolveTransactionTargetObjectIdsAsync(
            descriptor,
            updateElement,
            cancellationToken).ConfigureAwait(false);

        if (targetIds.Length > _editLimits.MaxFeaturesPerEdit)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Update action exceeds the maximum of {_editLimits.MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture)} matched features.",
                "Filter");
        }

        foreach (var objectId in targetIds)
        {
            var existing = await _featureReader.GetAsync(
                descriptor.StorageLayerId,
                objectId,
                cancellationToken).ConfigureAwait(false);
            if (!existing.HasValue)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Feature '{BuildFeatureId(descriptor, objectId)}' not found.",
                    "Filter");
            }

            var updatedFeature = await BuildTransactionUpdatedFeatureAsync(
                existing.Value,
                descriptor.Resource,
                changes,
                cancellationToken).ConfigureAwait(false);
            // Capture the request-intent flag BEFORE the merge result hides it: the
            // built feature preserves the existing WKB when the request omits geometry,
            // so reading updatedFeature.Geometry would over-report attribute-only Updates.
            operations.Add(new PreparedTransactionOperation(
                operations.Count,
                descriptor,
                TransactionActionKind.Update,
                FeatureEditOperation.Update(updatedFeature),
                handle,
                updatedFeature,
                null)
            {
                RequestGeometryChanged = changes.GeometrySpecified
            });
        }

        return null;
    }


    private async Task<IResult?> PrepareDeleteOperationsAsync(
        HttpContext context,
        XElement deleteElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = deleteElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var typeName = deleteElement.Attributes()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "typeName", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(attribute.Name.LocalName, "typeNames", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Delete action requires a typeName attribute.",
                "typeName");
        }

        var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, typeName);
        var validationError = await ValidateTransactionLayerWriteAccessAsync(
            context,
            descriptor.StorageLayerId,
            validatedLayerIds,
            cancellationToken).ConfigureAwait(false);
        if (validationError != null)
        {
            return validationError;
        }

        distinctLayerIds.Add(descriptor.StorageLayerId);

        var targetIds = await ResolveTransactionTargetObjectIdsAsync(
            descriptor,
            deleteElement,
            cancellationToken).ConfigureAwait(false);

        if (targetIds.Length > _editLimits.MaxFeaturesPerEdit)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Delete action exceeds the maximum of {_editLimits.MaxFeaturesPerEdit.ToString(CultureInfo.InvariantCulture)} matched features.",
                "Filter");
        }

        foreach (var objectId in targetIds)
        {
            var existing = await _featureReader.GetAsync(
                descriptor.StorageLayerId,
                objectId,
                cancellationToken).ConfigureAwait(false);
            if (!existing.HasValue)
            {
                return Wfs20ErrorResults.CreateBadRequest(
                    context,
                    "InvalidParameterValue",
                    $"Feature '{BuildFeatureId(descriptor, objectId)}' not found.",
                    "Filter");
            }

            operations.Add(new PreparedTransactionOperation(
                operations.Count,
                descriptor,
                TransactionActionKind.Delete,
                FeatureEditOperation.Delete(objectId),
                handle,
                null,
                existing.Value)
            {
                RequestGeometryChanged = false
            });
        }

        return null;
    }


    private async Task<IResult?> PrepareReplaceOperationsAsync(
        HttpContext context,
        XElement replaceElement,
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        ImmutableArray<PreparedTransactionOperation>.Builder operations,
        HashSet<int> distinctLayerIds,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        var handle = replaceElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "handle", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var featureElement = replaceElement.Elements()
            .FirstOrDefault(element => !string.Equals(element.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase));
        if (featureElement == null)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "MissingParameterValue",
                "Replace action must contain a replacement feature element.",
                "Replace");
        }

        var descriptor = ResolveTransactionFeatureTypeDescriptor(descriptors, featureElement.Name.LocalName);
        var validationError = await ValidateTransactionLayerWriteAccessAsync(
            context,
            descriptor.StorageLayerId,
            validatedLayerIds,
            cancellationToken).ConfigureAwait(false);
        if (validationError != null)
        {
            return validationError;
        }

        distinctLayerIds.Add(descriptor.StorageLayerId);

        var targetIds = await ResolveTransactionTargetObjectIdsAsync(
            descriptor,
            replaceElement,
            cancellationToken).ConfigureAwait(false);
        if (targetIds.Length != 1)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                "Replace action must identify exactly one target feature.",
                "Filter");
        }

        var existing = await _featureReader.GetAsync(
            descriptor.StorageLayerId,
            targetIds[0],
            cancellationToken).ConfigureAwait(false);
        if (!existing.HasValue)
        {
            return Wfs20ErrorResults.CreateBadRequest(
                context,
                "InvalidParameterValue",
                $"Feature '{BuildFeatureId(descriptor, targetIds[0])}' not found.",
                "Filter");
        }

        var replacement = await BuildTransactionReplaceFeatureAsync(
            descriptor.Resource,
            targetIds[0],
            featureElement,
            cancellationToken).ConfigureAwait(false);
        // Replace constructs a fresh feature from the request payload (or null when
        // the body omits geometry), and the operation overwrites the existing row.
        // Mark the change when either side has geometry so a body-less Replace that
        // clears an existing non-null geometry surfaces the cleared state to
        // consumers; the only no-change case is null-to-null, which we keep as
        // false. This avoids the under-report from inferring solely from the
        // replacement's WKB.
        operations.Add(new PreparedTransactionOperation(
            operations.Count,
            descriptor,
            TransactionActionKind.Replace,
            FeatureEditOperation.Update(replacement),
            handle,
            replacement,
            null)
        {
            RequestGeometryChanged = replacement.Geometry is { Length: > 0 }
                || existing.Value.Geometry is { Length: > 0 }
        });

        return null;
    }


    private async Task<Feature> BuildTransactionInsertFeatureAsync(
        MetadataV2Resource resource,
        XElement featureElement,
        CancellationToken cancellationToken)
    {
        var payload = ReadTransactionFeaturePayload(resource, featureElement);

        return await CreateTransactionFeatureAsync(
            resource,
            objectId: 0,
            payload.Geometry,
            payload.Attributes,
            cancellationToken).ConfigureAwait(false);
    }


    private async Task<Feature> BuildTransactionReplaceFeatureAsync(
        MetadataV2Resource resource,
        long objectId,
        XElement featureElement,
        CancellationToken cancellationToken)
    {
        var payload = ReadTransactionFeaturePayload(resource, featureElement);

        return await CreateTransactionFeatureAsync(
            resource,
            objectId,
            payload.Geometry,
            payload.Attributes,
            cancellationToken).ConfigureAwait(false);
    }


    private async Task<Feature> BuildTransactionUpdatedFeatureAsync(
        Feature existing,
        MetadataV2Resource resource,
        TransactionFeatureChanges changes,
        CancellationToken cancellationToken)
    {
        var attributes = existing.Attributes.ToBuilder();
        foreach (var (key, value) in changes.Attributes)
        {
            attributes[key] = value;
        }

        var geometry = changes.GeometrySpecified
            ? changes.Geometry
            : existing.Geometry;

        return await CreateTransactionFeatureAsync(
            resource,
            existing.Id,
            geometry,
            attributes.ToImmutable(),
            cancellationToken).ConfigureAwait(false);
    }


    private async Task<Feature> CreateTransactionFeatureAsync(
        MetadataV2Resource resource,
        long objectId,
        byte[]? geometry,
        ImmutableDictionary<string, object?> attributes,
        CancellationToken cancellationToken)
    {
        var geometryValidation = await _mutationValidator.ValidateGeometryAsync(
            geometry,
            cancellationToken).ConfigureAwait(false);
        if (!geometryValidation.IsValid)
        {
            throw new ArgumentException($"Geometry validation failed: {geometryValidation.ErrorMessage}");
        }

        var attributesResult = _mutationValidator.ValidateAttributes(
            resource,
            attributes,
            ValidationExtensions.AttributeValidationMode.Strict);
        if (!attributesResult.IsValid)
        {
            throw new ArgumentException(attributesResult.ErrorMessage ?? "Invalid attributes.");
        }

        return Feature.Create(objectId, geometryValidation.Geometry, attributesResult.Value!);
    }


    private static TransactionFeaturePayload ReadTransactionFeaturePayload(
        MetadataV2Resource resource,
        XElement featureElement)
    {
        var attributes = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        var gmlAssignedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? geometry = null;
        var geometryField = resource.FindPrimaryGeometryField();

        foreach (var propertyElement in featureElement.Elements())
        {
            if (TryResolveReservedGmlTransactionAttributeName(
                resource,
                propertyElement.Name.LocalName,
                propertyElement.Name.NamespaceName,
                out var reservedAttributeName))
            {
                attributes[reservedAttributeName] = ParseTransactionReservedAttributeValue(propertyElement);
                if (reservedAttributeName.Equals(ValidationExtensions.WfsGmlIdentifierAttributeName, StringComparison.Ordinal))
                {
                    attributes[ValidationExtensions.WfsGmlIdentifierCodeSpaceAttributeName] =
                        ParseTransactionGmlCodeSpace(propertyElement);
                }

                continue;
            }

            var resolution = ResolveTransactionField(
                resource,
                propertyElement.Name.LocalName,
                propertyElement.Name.NamespaceName);
            if (!resolution.HasValue)
            {
                continue;
            }

            var resolvedField = resolution.Value;

            if (geometryField != null &&
                resolvedField.Field.Name.Equals(geometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                geometry = ParseTransactionGeometryProperty(propertyElement, resource);
                continue;
            }

            if (resolvedField.IsGmlProperty)
            {
                gmlAssignedFields.Add(resolvedField.Field.Name);
                attributes[resolvedField.Field.Name] = ParseTransactionFieldValue(resolvedField.Field, propertyElement);
                continue;
            }

            if (gmlAssignedFields.Contains(resolvedField.Field.Name))
            {
                continue;
            }

            attributes[resolvedField.Field.Name] = ParseTransactionFieldValue(resolvedField.Field, propertyElement);
        }

        return new TransactionFeaturePayload(geometry, attributes.ToImmutable());
    }


    private static TransactionFeatureChanges ParseTransactionUpdateChanges(
        MetadataV2Resource resource,
        XElement updateElement)
    {
        var attributes = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        var propertyElements = updateElement.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Property", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (propertyElements.Length == 0)
        {
            throw new ArgumentException("Update action must include at least one Property element.");
        }

        byte[]? geometry = null;
        var geometrySpecified = false;
        var gmlAssignedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var geometryField = resource.FindPrimaryGeometryField();

        foreach (var propertyElement in propertyElements)
        {
            var nameElement = propertyElement.Elements()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "ValueReference", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase));
            if (nameElement == null)
            {
                continue;
            }

            var normalizedPropertyName = NormalizeTransactionPropertyReference(nameElement.Value);
            if (normalizedPropertyName.Equals("boundedBy", StringComparison.OrdinalIgnoreCase))
            {
                throw new WfsTransactionException(
                    "InvalidValue",
                    "Property 'gml:boundedBy' cannot be updated with the supplied value.",
                    "ValueReference");
            }

            var valueElement = propertyElement.Elements()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Value", StringComparison.OrdinalIgnoreCase));

            if (TryResolveReservedGmlTransactionAttributeName(
                resource,
                nameElement.Value.Trim(),
                namespaceName: null,
                out var reservedAttributeName))
            {
                attributes[reservedAttributeName] = valueElement == null
                    ? null
                    : ParseTransactionReservedAttributeValue(valueElement);
                if (reservedAttributeName.Equals(ValidationExtensions.WfsGmlIdentifierAttributeName, StringComparison.Ordinal))
                {
                    attributes[ValidationExtensions.WfsGmlIdentifierCodeSpaceAttributeName] = valueElement == null
                        ? null
                        : ParseTransactionGmlCodeSpace(valueElement);
                }

                continue;
            }

            var fieldResolution = ResolveTransactionField(resource, nameElement.Value.Trim());
            if (!fieldResolution.HasValue)
            {
                continue;
            }

            var resolvedField = fieldResolution.Value;

            if (geometryField != null &&
                resolvedField.Field.Name.Equals(geometryField.Name, StringComparison.OrdinalIgnoreCase))
            {
                geometrySpecified = true;
                geometry = valueElement == null
                    ? null
                    : ParseTransactionGeometryProperty(valueElement, resource);
                continue;
            }

            if (resolvedField.IsGmlProperty)
            {
                gmlAssignedFields.Add(resolvedField.Field.Name);
            }
            else if (gmlAssignedFields.Contains(resolvedField.Field.Name))
            {
                continue;
            }

            attributes[resolvedField.Field.Name] = valueElement == null
                ? null
                : ParseTransactionFieldValue(resolvedField.Field, valueElement);
        }

        if (attributes.Count == 0 && !geometrySpecified)
        {
            throw new ArgumentException("Update action must include at least one mutable property.");
        }

        return new TransactionFeatureChanges(geometrySpecified, geometry, attributes.ToImmutable());
    }


    private async Task<ImmutableArray<long>> ResolveTransactionTargetObjectIdsAsync(
        WfsFeatureTypeDescriptor descriptor,
        XElement actionElement,
        CancellationToken cancellationToken)
    {
        var filterElement = actionElement.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "Filter", StringComparison.OrdinalIgnoreCase));
        var filterChildren = filterElement?.Elements().ToArray() ?? [];
        var resourceIdValues = filterElement == null
            ? []
            : filterElement
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Attributes()
                    .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "rid", StringComparison.OrdinalIgnoreCase))
                    ?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();
        var resourceIds = resourceIdValues.Length == 0
            ? null
            : string.Join(',', resourceIdValues);
        var hasOnlyResourceIdPredicates = filterChildren.Length > 0 &&
            filterChildren.All(element => string.Equals(element.Name.LocalName, "ResourceId", StringComparison.OrdinalIgnoreCase));
        var filterXml = hasOnlyResourceIdPredicates
            ? null
            : filterElement?.ToString(SaveOptions.DisableFormatting);

        if (string.IsNullOrWhiteSpace(filterXml) && string.IsNullOrWhiteSpace(resourceIds))
        {
            throw new ArgumentException("Update and Delete actions must include a Filter or ResourceId.");
        }

        if (!hasOnlyResourceIdPredicates &&
            resourceIdValues.Length > 0)
        {
            throw new NotSupportedException("Transaction filters that combine ResourceId with other predicates are not yet supported.");
        }

        var query = await BuildFeatureQueryAsync(
            descriptor,
            propertyName: null,
            sortBy: null,
            bbox: null,
            filter: filterXml,
            resourceId: resourceIds,
            srsName: null,
            enforceResourceIdTypeMatch: true,
            requireResourceIdQualifier: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var objectIds = await _featureReader.QueryObjectIdsAsync(
            descriptor.StorageLayerId,
            query,
            cancellationToken).ConfigureAwait(false);

        if (objectIds.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Transaction filter did not match any features.");
        }

        return objectIds;
    }


    private static async Task<IResult?> ValidateTransactionLayerWriteAccessAsync(
        HttpContext context,
        int layerId,
        HashSet<int> validatedLayerIds,
        CancellationToken cancellationToken)
    {
        if (!validatedLayerIds.Add(layerId))
        {
            return null;
        }

        var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
            context,
            layerId,
            scope: AccessScope.Write,
            requiredProtocol: MetadataV2ServiceProtocols.Wfs20,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult;
        }

        if (IsAnonymousWriteAllowed(context, layerValidation.Resource!, layerValidation.Service))
        {
            return null;
        }

        return await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
            context,
            layerValidation.Resource!,
            layerValidation.Service,
            cancellationToken).ConfigureAwait(false);
    }


    private static WfsFeatureTypeDescriptor ResolveTransactionFeatureTypeDescriptor(
        IReadOnlyList<WfsFeatureTypeDescriptor> descriptors,
        string rawTypeName)
    {
        if (string.IsNullOrWhiteSpace(rawTypeName))
        {
            throw new WfsTransactionException("MissingParameterValue", "Transaction action is missing a feature type name.", "typeName");
        }

        var trimmedTypeName = rawTypeName.Trim();
        var localName = trimmedTypeName.Contains(':', StringComparison.Ordinal)
            ? trimmedTypeName[(trimmedTypeName.LastIndexOf(':') + 1)..]
            : trimmedTypeName;

        foreach (var descriptor in descriptors)
        {
            if (descriptor.QualifiedName.Equals(trimmedTypeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(trimmedTypeName, StringComparison.OrdinalIgnoreCase) ||
                descriptor.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor;
            }
        }

        throw new WfsTransactionException("InvalidValue", $"Unknown feature type '{rawTypeName}'.", "typeName");
    }


    private static TransactionFieldResolution? ResolveTransactionField(
        MetadataV2Resource resource,
        string rawName,
        string? namespaceName = null)
    {
        var normalizedName = NormalizeTransactionPropertyReference(rawName);
        if (TryResolveGmlTransactionField(resource, normalizedName, rawName, namespaceName, out var gmlField))
        {
            return gmlField == null
                ? null
                : new TransactionFieldResolution(gmlField, IsGmlProperty: true);
        }

        var resolvedName = FilterExpressionHelpers.ResolveFieldName(
            resource,
            normalizedName,
            allowGeometryAlias: true);
        if (resolvedName == null)
        {
            throw new ArgumentException($"Unknown property '{rawName}' for feature type '{GetResourceFeatureTypeName(resource)}'.");
        }

        var primaryKeyField = resource.FindPrimaryIdField();
        if (primaryKeyField != null &&
            resolvedName.Equals(primaryKeyField.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var resolvedField = resource.SchemaFields.FirstOrDefault(field =>
            field.Name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown property '{rawName}' for feature type '{GetResourceFeatureTypeName(resource)}'.");

        return new TransactionFieldResolution(resolvedField, IsGmlProperty: false);
    }


    private static string NormalizeTransactionPropertyReference(string rawName)
    {
        var normalized = rawName.Trim();
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < normalized.Length - 1)
        {
            normalized = normalized[(lastSlash + 1)..];
        }

        if (normalized.StartsWith('@'))
        {
            normalized = normalized[1..];
        }

        var predicateIndex = normalized.IndexOf('[');
        if (predicateIndex > 0)
        {
            normalized = normalized[..predicateIndex];
        }

        var colonIndex = normalized.LastIndexOf(':');
        if (colonIndex >= 0 && colonIndex < normalized.Length - 1)
        {
            normalized = normalized[(colonIndex + 1)..];
        }

        return normalized.Trim();
    }


    private static bool TryResolveReservedGmlTransactionAttributeName(
        MetadataV2Resource resource,
        string rawName,
        string? namespaceName,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? attributeName)
    {
        attributeName = null;

        var isGmlProperty = string.Equals(namespaceName, Wfs20Utilities.GmlNamespace, StringComparison.Ordinal) ||
            rawName.StartsWith("gml:", StringComparison.OrdinalIgnoreCase);
        if (!isGmlProperty)
        {
            return false;
        }

        var normalizedName = NormalizeTransactionPropertyReference(rawName);
        var localName = normalizedName.Contains(':', StringComparison.Ordinal)
            ? normalizedName[(normalizedName.LastIndexOf(':') + 1)..]
            : normalizedName;

        attributeName = localName.ToLowerInvariant() switch
        {
            "identifier" => ValidationExtensions.WfsGmlIdentifierAttributeName,
            "name" when !HasTransactionAttributeField(resource, "name") => ValidationExtensions.WfsGmlNameAttributeName,
            "description" when !HasTransactionAttributeField(resource, "description") => ValidationExtensions.WfsGmlDescriptionAttributeName,
            _ => null
        };

        return attributeName != null;
    }


    private static bool HasTransactionAttributeField(MetadataV2Resource resource, string fieldName)
    {
        return resource.SchemaFields.Any(candidate =>
            candidate.Type is not MetadataV2FieldType.Geometry and not MetadataV2FieldType.Geography &&
            candidate.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
    }


    private static bool TryResolveGmlTransactionField(
        MetadataV2Resource resource,
        string normalizedName,
        string rawName,
        string? namespaceName,
        out MetadataV2Field? field)
    {
        field = null;

        var isGmlProperty = string.Equals(namespaceName, Wfs20Utilities.GmlNamespace, StringComparison.Ordinal) ||
            rawName.StartsWith("gml:", StringComparison.OrdinalIgnoreCase);
        if (!isGmlProperty)
        {
            return false;
        }

        var localName = normalizedName.Contains(':', StringComparison.Ordinal)
            ? normalizedName[(normalizedName.LastIndexOf(':') + 1)..]
            : normalizedName;

        var mappedFieldName = localName.ToLowerInvariant() switch
        {
            "name" => "name",
            "description" => "description",
            "identifier" => null,
            _ => null
        };
        if (mappedFieldName == null)
        {
            return true;
        }

        field = resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(mappedFieldName, StringComparison.OrdinalIgnoreCase));
        var primaryKeyField = resource.FindPrimaryIdField();
        if (field != null &&
            primaryKeyField != null &&
            field.Name.Equals(primaryKeyField.Name, StringComparison.OrdinalIgnoreCase))
        {
            field = null;
        }

        return true;
    }


    private static object? ParseTransactionFieldValue(MetadataV2Field field, XElement valueElement)
    {
        if (HasNilAttribute(valueElement))
        {
            return null;
        }

        var rawValue = valueElement.Value.Trim();
        return field.Type switch
        {
            MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger
                => long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue)
                    ? integerValue
                    : rawValue,
            MetadataV2FieldType.Double or MetadataV2FieldType.Float
                => double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue)
                    ? numericValue
                    : rawValue,
            MetadataV2FieldType.Boolean
                => bool.TryParse(rawValue, out var booleanValue)
                    ? booleanValue
                    : rawValue,
            _ => rawValue
        };
    }


    private static string? ParseTransactionReservedAttributeValue(XElement valueElement)
    {
        if (HasNilAttribute(valueElement))
        {
            return null;
        }

        return valueElement.Value.Trim();
    }


    /// <summary>
    /// Extracts the <c>codeSpace</c> attribute carried by a <c>gml:identifier</c> property so it can
    /// be persisted and round-tripped. <c>gml:identifier</c> is of type
    /// <c>gml:CodeWithAuthorityType</c>, for which <c>codeSpace</c> is mandatory; the WFS 2.0 ETS
    /// asserts the standard property survives a Replace round-trip and validates it against the GML
    /// schema, so dropping the authority would emit schema-invalid GML on read-back.
    /// </summary>
    private static string? ParseTransactionGmlCodeSpace(XElement valueElement)
    {
        var direct = valueElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "codeSpace", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        // For Update payloads the identifier may be wrapped inside a gml:identifier child of wfs:Value.
        var nested = valueElement.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "identifier", StringComparison.OrdinalIgnoreCase))
            ?.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "codeSpace", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(nested) ? null : nested.Trim();
    }


    private static byte[]? ParseTransactionGeometryProperty(
        XElement propertyElement,
        MetadataV2Resource resource)
    {
        if (HasNilAttribute(propertyElement))
        {
            return null;
        }

        var geometryElement = propertyElement.Name.NamespaceName == Wfs20Utilities.GmlNamespace
            ? propertyElement
            : propertyElement.Elements()
                .FirstOrDefault(element => string.Equals(element.Name.NamespaceName, Wfs20Utilities.GmlNamespace, StringComparison.Ordinal));

        if (geometryElement == null)
        {
            if (propertyElement.IsEmpty)
            {
                return null;
            }

            throw new ArgumentException($"Geometry property '{propertyElement.Name.LocalName}' must contain a GML geometry.");
        }

        var defaultSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var geometry = ParseTransactionGeometry(geometryElement, defaultSrid);
        return GeometryWkbWriter.Write(geometry);
    }

    private static string GetResourceFeatureTypeName(MetadataV2Resource resource)
    {
        if (!string.IsNullOrWhiteSpace(resource.Metadata.Name))
        {
            return resource.Metadata.Name;
        }

        if (!string.IsNullOrWhiteSpace(resource.Metadata.Id))
        {
            return resource.Metadata.Id;
        }

        return "resource";
    }


    private static Geometry ParseTransactionGeometry(XElement geometryElement, int defaultSrid)
    {
        var crsDefinition = geometryElement.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "srsName", StringComparison.OrdinalIgnoreCase))
            ?.Value is { Length: > 0 } srsName &&
            SpatialReferenceHelpers.TryParseCrsDefinition(srsName, out var parsedDefinition)
            ? parsedDefinition
            : SpatialReferenceHelpers.TryParseCrsDefinition(defaultSrid.ToString(CultureInfo.InvariantCulture), out var defaultDefinition)
                ? defaultDefinition
                : new CrsDefinition(
                    FormattableString.Invariant($"http://www.opengis.net/def/crs/EPSG/0/{defaultSrid}"),
                    defaultSrid,
                    AxisOrder.EastNorth,
                    IsGeographic: false);

        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(crsDefinition.Srid);

        return geometryElement.Name.LocalName switch
        {
            "Point" => ParseTransactionPointGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "MultiPoint" => ParseTransactionMultiPointGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "LineString" or "Curve" => ParseTransactionLineStringGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "MultiLineString" or "MultiCurve" => ParseTransactionMultiLineStringGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "Polygon" or "Surface" => ParseTransactionPolygonGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            "MultiPolygon" or "MultiSurface" => ParseTransactionMultiPolygonGeometry(geometryElement, geometryFactory, crsDefinition.AxisOrder),
            _ => throw new NotSupportedException($"Unsupported GML geometry type '{geometryElement.Name.LocalName}'.")
        };
    }


    private static Point ParseTransactionPointGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var positionElement = geometryElement.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "pos", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Point geometry must contain a gml:pos element.");

        return geometryFactory.CreatePoint(ParseTransactionCoordinate(positionElement.Value, axisOrder));
    }


    private static MultiPoint ParseTransactionMultiPointGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var points = geometryElement
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "Point", StringComparison.OrdinalIgnoreCase))
            .Select(pointElement => ParseTransactionPointGeometry(pointElement, geometryFactory, axisOrder))
            .ToArray();

        if (points.Length == 0)
        {
            throw new ArgumentException("MultiPoint geometry must contain at least one gml:Point element.");
        }

        return geometryFactory.CreateMultiPoint(points);
    }


    private static LineString ParseTransactionLineStringGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var coordinates = ParseTransactionCoordinateSequence(geometryElement, axisOrder);
        if (coordinates.Length < 2)
        {
            throw new ArgumentException("LineString geometry must contain at least two positions.");
        }

        return geometryFactory.CreateLineString(coordinates);
    }


    private static MultiLineString ParseTransactionMultiLineStringGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var lineStrings = geometryElement
            .Descendants()
            .Where(element =>
                string.Equals(element.Name.LocalName, "LineString", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Name.LocalName, "Curve", StringComparison.OrdinalIgnoreCase))
            .Select(lineElement => ParseTransactionLineStringGeometry(lineElement, geometryFactory, axisOrder))
            .ToArray();

        if (lineStrings.Length == 0)
        {
            throw new ArgumentException("MultiLineString geometry must contain at least one line geometry.");
        }

        return geometryFactory.CreateMultiLineString(lineStrings);
    }


    private static Polygon ParseTransactionPolygonGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        XElement? polygonSource = geometryElement;
        if (string.Equals(geometryElement.Name.LocalName, "Surface", StringComparison.OrdinalIgnoreCase))
        {
            polygonSource = geometryElement
                .Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "PolygonPatch", StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Surface geometry must contain a PolygonPatch element.");
        }

        var exteriorElement = polygonSource
            .Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "exterior", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Polygon geometry must contain an exterior ring.");

        var shell = geometryFactory.CreateLinearRing(ParseTransactionLinearRingCoordinates(exteriorElement, axisOrder));
        var holes = polygonSource
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "interior", StringComparison.OrdinalIgnoreCase))
            .Select(interiorElement => geometryFactory.CreateLinearRing(ParseTransactionLinearRingCoordinates(interiorElement, axisOrder)))
            .ToArray();

        return geometryFactory.CreatePolygon(shell, holes);
    }


    private static MultiPolygon ParseTransactionMultiPolygonGeometry(
        XElement geometryElement,
        GeometryFactory geometryFactory,
        AxisOrder axisOrder)
    {
        var polygons = geometryElement
            .Descendants()
            .Where(element =>
                string.Equals(element.Name.LocalName, "Polygon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Name.LocalName, "Surface", StringComparison.OrdinalIgnoreCase))
            .Select(polygonElement => ParseTransactionPolygonGeometry(polygonElement, geometryFactory, axisOrder))
            .ToArray();

        if (polygons.Length == 0)
        {
            throw new ArgumentException("MultiPolygon geometry must contain at least one polygon geometry.");
        }

        return geometryFactory.CreateMultiPolygon(polygons);
    }


    private static Coordinate[] ParseTransactionLinearRingCoordinates(XElement ringContainer, AxisOrder axisOrder)
    {
        var linearRingElement = ringContainer.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "LinearRing", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Polygon ring must contain a LinearRing element.");
        var coordinates = ParseTransactionCoordinateSequence(linearRingElement, axisOrder);
        if (coordinates.Length < 4)
        {
            throw new ArgumentException("Polygon rings must contain at least four positions.");
        }

        return coordinates;
    }


    private static Coordinate[] ParseTransactionCoordinateSequence(XElement geometryElement, AxisOrder axisOrder)
    {
        var posListElement = geometryElement.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "posList", StringComparison.OrdinalIgnoreCase));
        if (posListElement != null)
        {
            return ParseTransactionPosList(posListElement.Value, axisOrder);
        }

        var positions = geometryElement.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "pos", StringComparison.OrdinalIgnoreCase))
            .Select(element => ParseTransactionCoordinate(element.Value, axisOrder))
            .ToArray();
        if (positions.Length > 0)
        {
            return positions;
        }

        throw new ArgumentException($"Geometry '{geometryElement.Name.LocalName}' must contain gml:pos or gml:posList coordinates.");
    }


    private static Coordinate[] ParseTransactionPosList(string rawPosList, AxisOrder axisOrder)
    {
        var ordinates = rawPosList
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();

        if (ordinates.Length < 2 || ordinates.Length % 2 != 0)
        {
            throw new ArgumentException("GML posList must contain an even number of ordinates.");
        }

        var coordinates = new Coordinate[ordinates.Length / 2];
        for (var index = 0; index < ordinates.Length; index += 2)
        {
            coordinates[index / 2] = CreateTransactionCoordinate(ordinates[index], ordinates[index + 1], axisOrder);
        }

        return coordinates;
    }


    private static Coordinate ParseTransactionCoordinate(string rawPosition, AxisOrder axisOrder)
    {
        var ordinates = rawPosition
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        if (ordinates.Length < 2)
        {
            throw new ArgumentException("GML position must contain at least two ordinates.");
        }

        return CreateTransactionCoordinate(ordinates[0], ordinates[1], axisOrder);
    }


    private static Coordinate CreateTransactionCoordinate(double first, double second, AxisOrder axisOrder)
    {
        return axisOrder == AxisOrder.NorthEast
            ? new Coordinate(second, first)
            : new Coordinate(first, second);
    }


    private static bool HasNilAttribute(XElement element)
    {
        return element.Attributes().Any(attribute =>
            string.Equals(attribute.Name.LocalName, "nil", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(attribute.Value, out var isNil) &&
            isNil);
    }


    private static async Task<XDocument> ReadTransactionDocumentAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HttpContext.Items.TryGetValue(Wfs20DispatcherEndpoint.ParsedXmlDocumentItemKey, out var parsedDocument) &&
            parsedDocument is XDocument document)
        {
            return document;
        }

        request.EnableBuffering();
        request.Body.Position = 0;
        var bodyRead = await RequestBodySizeGuard.ReadUtf8TextAsync(
            request.HttpContext,
            RequestBodySizeGuard.ResolveMaxBodyBytes(request.HttpContext),
            cancellationToken).ConfigureAwait(false);
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        if (bodyRead.TooLarge)
        {
            throw new RequestBodyTooLargeException();
        }

        var body = bodyRead.Body;

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidDataException("Transaction requests require an XML request body.");
        }

        try
        {
            return SecureXmlDocumentParser.Parse(body, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Invalid WFS Transaction XML request body.", ex);
        }
    }


    private static bool ResolveRollbackOnFailure(HttpRequest request, XElement root)
    {
        var rawValue = request.Query["rollbackOnFailure"].FirstOrDefault()
            ?? request.Query["ROLLBACKONFAILURE"].FirstOrDefault()
            ?? root.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "rollbackOnFailure", StringComparison.OrdinalIgnoreCase))
                ?.Value;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        return bool.TryParse(rawValue, out var parsed)
            ? parsed
            : throw new ArgumentException("rollbackOnFailure must be a boolean value.");
    }


    private static string GetFirstTransactionError(FeatureEditResult editResult)
    {
        return editResult.CreateResults.Concat(editResult.UpdateResults).Concat(editResult.DeleteResults)
            .FirstOrDefault(result => !result.IsSuccess)
            .ErrorMessage
            ?? "Transaction failed.";
    }


    private async Task<FeatureEditResult> ApplyPreparedTransactionAsync(
        HttpContext context,
        TransactionPreparationResult prepared,
        bool rollbackOnFailure,
        CancellationToken cancellationToken)
    {
        if (prepared.LayerIdCount <= 1)
        {
            // Pass each operation's request-intent geometry flag alongside the
            // EditOperation so the outbox payload tracks the request rather than the
            // post-merge feature WKB (which BuildTransactionUpdatedFeatureAsync preserves
            // when the request omits geometry).
            var operations = prepared.Operations
                .Select(static operation => operation.EditOperation)
                .ToImmutableArray();
            var requestGeometryChangedFlags = prepared.Operations
                .Select(static operation => operation.RequestGeometryChanged)
                .ToImmutableArray();
            var resource = prepared.Operations.IsDefaultOrEmpty
                ? throw new InvalidOperationException("WFS transaction did not prepare any operations.")
                : prepared.Operations[0].Descriptor.Resource;
            return await ExecutePreparedEditAsync(
                context,
                prepared.LayerId,
                resource,
                operations,
                requestGeometryChangedFlags,
                rollbackOnFailure,
                cancellationToken).ConfigureAwait(false);
        }

        var createResultIndexes = new Dictionary<int, int>();
        var updateResultIndexes = new Dictionary<int, int>();
        var deleteResultIndexes = new Dictionary<int, int>();
        var createResultIndex = 0;
        var updateResultIndex = 0;
        var deleteResultIndex = 0;

        foreach (var operation in prepared.Operations)
        {
            switch (operation.EditOperation.Kind)
            {
                case FeatureEditOperationKind.Create:
                    createResultIndexes[operation.Sequence] = createResultIndex++;
                    break;

                case FeatureEditOperationKind.Update:
                    updateResultIndexes[operation.Sequence] = updateResultIndex++;
                    break;

                case FeatureEditOperationKind.Delete:
                    deleteResultIndexes[operation.Sequence] = deleteResultIndex++;
                    break;
            }
        }

        var aggregatedCreateResults = new EditOperationResult[prepared.InsertCount];
        var aggregatedUpdateResults = new EditOperationResult[prepared.UpdateCount + prepared.ReplaceCount];
        var aggregatedDeleteResults = new EditOperationResult[prepared.DeleteCount];
        var createdIdSlots = new long?[prepared.InsertCount];
        var createdCount = 0;
        var updatedCount = 0;
        var deletedCount = 0;

        foreach (var layerGroup in prepared.Operations
                     .GroupBy(static operation => operation.Descriptor.StorageLayerId)
                     .OrderBy(static group => group.Min(operation => operation.Sequence)))
        {
            var operations = layerGroup.OrderBy(static operation => operation.Sequence).ToImmutableArray();
            var resource = operations[0].Descriptor.Resource;
            // Per-operation request-intent geometry flags, ordered to match the
            // EditOperation sequence the data layer iterates.
            var layerResult = await ExecutePreparedEditAsync(
                context,
                layerGroup.Key,
                resource,
                operations
                    .Select(static operation => operation.EditOperation)
                    .ToImmutableArray(),
                operations
                    .Select(static operation => operation.RequestGeometryChanged)
                    .ToImmutableArray(),
                rollbackOnFailure: false,
                cancellationToken).ConfigureAwait(false);

            createdCount += layerResult.CreatedCount;
            updatedCount += layerResult.UpdatedCount;
            deletedCount += layerResult.DeletedCount;

            var localCreateIndex = 0;
            var localUpdateIndex = 0;
            var localDeleteIndex = 0;

            foreach (var operation in operations)
            {
                switch (operation.EditOperation.Kind)
                {
                    case FeatureEditOperationKind.Create:
                        {
                            var result = localCreateIndex < layerResult.CreateResults.Length
                                ? layerResult.CreateResults[localCreateIndex]
                                : default;
                            localCreateIndex++;

                            var globalIndex = createResultIndexes[operation.Sequence];
                            aggregatedCreateResults[globalIndex] = result;
                            if (result.IsSuccess && result.ObjectId.HasValue)
                            {
                                createdIdSlots[globalIndex] = result.ObjectId.Value;
                            }

                            break;
                        }

                    case FeatureEditOperationKind.Update:
                        {
                            var result = localUpdateIndex < layerResult.UpdateResults.Length
                                ? layerResult.UpdateResults[localUpdateIndex]
                                : default;
                            localUpdateIndex++;

                            aggregatedUpdateResults[updateResultIndexes[operation.Sequence]] = result;
                            break;
                        }

                    case FeatureEditOperationKind.Delete:
                        {
                            var result = localDeleteIndex < layerResult.DeleteResults.Length
                                ? layerResult.DeleteResults[localDeleteIndex]
                                : default;
                            localDeleteIndex++;

                            aggregatedDeleteResults[deleteResultIndexes[operation.Sequence]] = result;
                            break;
                        }
                }
            }
        }

        return FeatureEditResult.Success(
            createdCount,
            updatedCount,
            deletedCount,
            createdIds: createdIdSlots
                .Where(static id => id.HasValue)
                .Select(static id => id!.Value)
                .ToImmutableArray(),
            createResults: aggregatedCreateResults.ToImmutableArray(),
            updateResults: aggregatedUpdateResults.ToImmutableArray(),
            deleteResults: aggregatedDeleteResults.ToImmutableArray());
    }

    private async Task<FeatureEditResult> ExecutePreparedEditAsync(
        HttpContext context,
        int layerId,
        MetadataV2Resource resource,
        ImmutableArray<FeatureEditOperation> operations,
        ImmutableArray<bool> requestGeometryChangedFlags,
        bool rollbackOnFailure,
        CancellationToken cancellationToken)
    {
        var editAdapterResult = await _editParameterAdapter.ConvertAsync(
            new Wfs20EditRequest
            {
                Operations = operations,
                RollbackOnFailure = rollbackOnFailure
            },
            cancellationToken).ConfigureAwait(false);
        if (!editAdapterResult.IsSuccess || editAdapterResult.EditRequest == null)
        {
            throw new InvalidOperationException(editAdapterResult.ErrorMessage ?? "Invalid WFS transaction.");
        }

        var optimizedEdit = _editProcessor.OptimizeEdit(editAdapterResult.EditRequest.Value, resource);
        var editValidation = _editProcessor.ValidateEdit(optimizedEdit, resource);
        if (!editValidation.IsValid)
        {
            throw new InvalidOperationException(editValidation.ErrorMessage ?? "Invalid WFS transaction.");
        }

        var editBatch = _editProcessor.ToFeatureEditBatch(optimizedEdit, resource);
        // Bin per-kind geometry-change flags from the request-intent flags captured by
        // PrepareInsert/Update/Delete/ReplaceOperationsAsync (BEFORE merging with the
        // existing row). Reading editBatch.Operations[i].Feature.Geometry would over-
        // report attribute-only Updates because BuildTransactionUpdatedFeatureAsync
        // preserves the existing WKB when changes.GeometrySpecified is false.
        var perOperationGeometryChanged = BuildPerOperationGeometryChanged(operations, requestGeometryChangedFlags);
        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            layerId,
            HonuaTelemetry.Protocols.Wfs20,
            serviceProtocol: MetadataV2ServiceProtocols.Wfs20,
            layerSrid: resource.ReadSrid(),
            perOperationGeometryChanged: perOperationGeometryChanged,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var outboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);
        return await _featureWriter.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bin per-operation request-intent geometry flags by operation kind. The flags must
    /// be supplied in the same order the operations appear in <paramref name="operations"/>
    /// (which is the order ApplyOrderedEditsAsync dequeues from each kind's queue, since
    /// it iterates ordered operations and dequeues from the matching kind queue per row).
    /// Deletes always report false.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<bool>>? BuildPerOperationGeometryChanged(
        ImmutableArray<FeatureEditOperation> operations,
        ImmutableArray<bool> requestGeometryChangedFlags)
    {
        if (operations.IsDefaultOrEmpty)
        {
            return null;
        }

        var creates = new List<bool>();
        var updates = new List<bool>();
        var deletes = new List<bool>();

        for (var i = 0; i < operations.Length; i++)
        {
            var flag = i < requestGeometryChangedFlags.Length && requestGeometryChangedFlags[i];
            switch (operations[i].Kind)
            {
                case FeatureEditOperationKind.Create:
                    creates.Add(flag);
                    break;
                case FeatureEditOperationKind.Update:
                    updates.Add(flag);
                    break;
                case FeatureEditOperationKind.Delete:
                    deletes.Add(false);
                    break;
            }
        }

        if (creates.Count == 0 && updates.Count == 0 && deletes.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal);
        if (creates.Count > 0) result["create"] = creates;
        if (updates.Count > 0) result["update"] = updates;
        if (deletes.Count > 0) result["delete"] = deletes;
        return result;
    }


    private static async Task<IReadOnlyDictionary<int, string>> ResolveTransactionServiceIdsAsync(
        HttpContext context,
        TransactionPreparationResult prepared,
        CancellationToken cancellationToken)
    {
        var serviceIdsByLayer = new Dictionary<int, string>();
        foreach (var layerId in prepared.Operations
                     .Select(static operation => operation.Descriptor.StorageLayerId)
                     .Distinct())
        {
            serviceIdsByLayer[layerId] = await FeatureMutationEventService.ResolveServiceIdAsync(
                context,
                layerId,
                MetadataV2ServiceProtocols.Wfs20,
                cancellationToken).ConfigureAwait(false);
        }

        return serviceIdsByLayer;
    }


    private async Task PublishTransactionEventsAsync(
        HttpContext context,
        TransactionPreparationResult prepared,
        FeatureEditResult editResult,
        IReadOnlyDictionary<int, string> serviceIdsByLayer,
        CancellationToken cancellationToken)
    {
        var createIndex = 0;
        var updateIndex = 0;
        var deleteIndex = 0;

        foreach (var operation in prepared.Operations)
        {
            switch (operation.EditOperation.Kind)
            {
                case FeatureEditOperationKind.Create:
                    {
                        var result = createIndex < editResult.CreateResults.Length
                            ? editResult.CreateResults[createIndex]
                            : default;
                        createIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue || operation.MutationFeature == null)
                        {
                            break;
                        }

                        var layerId = operation.Descriptor.StorageLayerId;
                        var createdFeature = operation.MutationFeature.Value with { Id = result.ObjectId.Value };
                        // WFS GeometryWkbWriter does not embed SRID, so thread the resource
                        // SRID through as the enrichment fallback.
                        var layerSrid = operation.Descriptor.Resource.ReadSrid();
                        await _mutationEventService.PublishAsync(
                            context,
                            layerId,
                            result.ObjectId.Value,
                            "create",
                            HonuaTelemetry.Protocols.Wfs20,
                            cancellationToken,
                            mutationFeature: createdFeature,
                            serviceId: serviceIdsByLayer[layerId],
                            serviceProtocol: MetadataV2ServiceProtocols.Wfs20,
                            layerSrid: layerSrid).ConfigureAwait(false);
                        break;
                    }

                case FeatureEditOperationKind.Update:
                    {
                        var result = updateIndex < editResult.UpdateResults.Length
                            ? editResult.UpdateResults[updateIndex]
                            : default;
                        updateIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue || operation.MutationFeature == null)
                        {
                            break;
                        }

                        var layerId = operation.Descriptor.StorageLayerId;
                        var layerSrid = operation.Descriptor.Resource.ReadSrid();
                        await _mutationEventService.PublishAsync(
                            context,
                            layerId,
                            result.ObjectId.Value,
                            "update",
                            HonuaTelemetry.Protocols.Wfs20,
                            cancellationToken,
                            mutationFeature: operation.MutationFeature.Value,
                            serviceId: serviceIdsByLayer[layerId],
                            serviceProtocol: MetadataV2ServiceProtocols.Wfs20,
                            layerSrid: layerSrid).ConfigureAwait(false);
                        break;
                    }

                case FeatureEditOperationKind.Delete:
                    {
                        var result = deleteIndex < editResult.DeleteResults.Length
                            ? editResult.DeleteResults[deleteIndex]
                            : default;
                        deleteIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue)
                        {
                            break;
                        }

                        var layerId = operation.Descriptor.StorageLayerId;
                        var layerSrid = operation.Descriptor.Resource.ReadSrid();
                        await _mutationEventService.PublishAsync(
                            context,
                            layerId,
                            result.ObjectId.Value,
                            "delete",
                            HonuaTelemetry.Protocols.Wfs20,
                            cancellationToken,
                            mutationFeature: operation.DeleteSnapshot,
                            serviceId: serviceIdsByLayer[layerId],
                            serviceProtocol: MetadataV2ServiceProtocols.Wfs20,
                            layerSrid: layerSrid).ConfigureAwait(false);
                        break;
                    }
            }
        }
    }


    private static string BuildTransactionResponseXml(
        TransactionPreparationResult prepared,
        FeatureEditResult editResult)
    {
        var inserted = new List<(PreparedTransactionOperation Operation, EditOperationResult Result)>();
        var replaced = new List<(PreparedTransactionOperation Operation, EditOperationResult Result)>();
        var updatedCount = 0;
        var deletedCount = 0;
        var createResultIndex = 0;
        var updateResultIndex = 0;
        var deleteResultIndex = 0;

        foreach (var operation in prepared.Operations)
        {
            switch (operation.EditOperation.Kind)
            {
                case FeatureEditOperationKind.Create:
                    {
                        var result = createResultIndex < editResult.CreateResults.Length
                            ? editResult.CreateResults[createResultIndex]
                            : default;
                        createResultIndex++;

                        if (result.IsSuccess && result.ObjectId.HasValue)
                        {
                            inserted.Add((operation, result));
                        }

                        break;
                    }

                case FeatureEditOperationKind.Update:
                    {
                        var result = updateResultIndex < editResult.UpdateResults.Length
                            ? editResult.UpdateResults[updateResultIndex]
                            : default;
                        updateResultIndex++;

                        if (!result.IsSuccess || !result.ObjectId.HasValue)
                        {
                            break;
                        }

                        if (operation.ActionKind == TransactionActionKind.Replace)
                        {
                            replaced.Add((operation, result));
                        }
                        else
                        {
                            updatedCount++;
                        }

                        break;
                    }

                case FeatureEditOperationKind.Delete:
                    {
                        var result = deleteResultIndex < editResult.DeleteResults.Length
                            ? editResult.DeleteResults[deleteResultIndex]
                            : default;
                        deleteResultIndex++;

                        if (result.IsSuccess)
                        {
                            deletedCount++;
                        }

                        break;
                    }
            }
        }

        return WriteXmlDocument(writer =>
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("wfs", "TransactionResponse", Wfs20Utilities.WfsNamespace);
            writer.WriteAttributeString("xmlns", "fes", null, Wfs20Utilities.FesNamespace);
            writer.WriteAttributeString("version", Wfs20Utilities.Version);
            writer.WriteAttributeString("timeStamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            writer.WriteStartElement("wfs", "TransactionSummary", Wfs20Utilities.WfsNamespace);
            if (prepared.InsertCount > 0)
            {
                writer.WriteElementString("wfs", "totalInserted", Wfs20Utilities.WfsNamespace, inserted.Count.ToString(CultureInfo.InvariantCulture));
            }

            if (prepared.UpdateCount > 0)
            {
                writer.WriteElementString("wfs", "totalUpdated", Wfs20Utilities.WfsNamespace, updatedCount.ToString(CultureInfo.InvariantCulture));
            }

            if (prepared.ReplaceCount > 0)
            {
                writer.WriteElementString("wfs", "totalReplaced", Wfs20Utilities.WfsNamespace, replaced.Count.ToString(CultureInfo.InvariantCulture));
            }

            if (prepared.DeleteCount > 0)
            {
                writer.WriteElementString("wfs", "totalDeleted", Wfs20Utilities.WfsNamespace, deletedCount.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndElement();

            if (inserted.Count > 0)
            {
                writer.WriteStartElement("wfs", "InsertResults", Wfs20Utilities.WfsNamespace);
                foreach (var (operation, result) in inserted)
                {
                    var objectId = result.ObjectId ?? 0;
                    writer.WriteStartElement("wfs", "Feature", Wfs20Utilities.WfsNamespace);
                    if (!string.IsNullOrWhiteSpace(operation.Handle))
                    {
                        writer.WriteAttributeString("handle", operation.Handle);
                    }

                    WriteTransactionResourceId(writer, operation.Descriptor, objectId);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            if (replaced.Count > 0)
            {
                writer.WriteStartElement("wfs", "ReplaceResults", Wfs20Utilities.WfsNamespace);
                foreach (var (operation, result) in replaced)
                {
                    var objectId = result.ObjectId ?? 0;
                    writer.WriteStartElement("wfs", "Feature", Wfs20Utilities.WfsNamespace);
                    if (!string.IsNullOrWhiteSpace(operation.Handle))
                    {
                        writer.WriteAttributeString("handle", operation.Handle);
                    }

                    WriteTransactionResourceId(writer, operation.Descriptor, objectId);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        });
    }


    private static void WriteTransactionResourceId(
        XmlWriter writer,
        WfsFeatureTypeDescriptor descriptor,
        long objectId)
    {
        writer.WriteStartElement("fes", "ResourceId", Wfs20Utilities.FesNamespace);
        writer.WriteAttributeString("rid", BuildFeatureId(descriptor, objectId));
        writer.WriteEndElement();
    }


    private sealed class WfsTransactionException(
        string exceptionCode,
        string message,
        string? locator = null) : Exception(message)
    {
        public string ExceptionCode { get; } = exceptionCode;

        public string? Locator { get; } = locator;
    }


    private enum TransactionActionKind
    {
        Insert,
        Update,
        Replace,
        Delete
    }


    private sealed record PreparedTransactionOperation(
        int Sequence,
        WfsFeatureTypeDescriptor Descriptor,
        TransactionActionKind ActionKind,
        FeatureEditOperation EditOperation,
        string? Handle,
        Feature? MutationFeature,
        Feature? DeleteSnapshot)
    {
        /// <summary>
        /// True when the originating request body explicitly specified a geometry. For
        /// Insert/Replace this is always true (the request must carry the feature payload);
        /// for Update this reflects <c>changes.GeometrySpecified</c> captured BEFORE merging
        /// with the existing row, since <c>BuildTransactionUpdatedFeatureAsync</c> preserves
        /// the existing WKB when the request omits geometry. Delete operations report false.
        /// </summary>
        public bool RequestGeometryChanged { get; init; }
    }

    private readonly record struct TransactionFieldResolution(
        MetadataV2Field Field,
        bool IsGmlProperty);

    private readonly record struct TransactionFeaturePayload(
        byte[]? Geometry,
        ImmutableDictionary<string, object?> Attributes);

    private readonly record struct TransactionFeatureChanges(
        bool GeometrySpecified,
        byte[]? Geometry,
        ImmutableDictionary<string, object?> Attributes);

    private readonly record struct TransactionPreparationResult(
        ImmutableArray<PreparedTransactionOperation> Operations,
        int LayerId,
        int LayerIdCount,
        IResult? ErrorResult,
        int InsertCount,
        int UpdateCount,
        int ReplaceCount,
        int DeleteCount)
    {
        public bool IsValid => ErrorResult is null;

        public static TransactionPreparationResult Success(
            ImmutableArray<PreparedTransactionOperation> operations,
            int layerId,
            int layerIdCount)
        {
            var insertCount = 0;
            var updateCount = 0;
            var replaceCount = 0;
            var deleteCount = 0;

            foreach (var operation in operations)
            {
                switch (operation.ActionKind)
                {
                    case TransactionActionKind.Insert:
                        insertCount++;
                        break;
                    case TransactionActionKind.Update:
                        updateCount++;
                        break;
                    case TransactionActionKind.Replace:
                        replaceCount++;
                        break;
                    case TransactionActionKind.Delete:
                        deleteCount++;
                        break;
                }
            }

            return new TransactionPreparationResult(
                operations,
                layerId,
                layerIdCount,
                null,
                insertCount,
                updateCount,
                replaceCount,
                deleteCount);
        }

        public static TransactionPreparationResult Failure(IResult errorResult)
            => new(
                ImmutableArray<PreparedTransactionOperation>.Empty,
                0,
                0,
                errorResult,
                0,
                0,
                0,
                0);
    }
}
