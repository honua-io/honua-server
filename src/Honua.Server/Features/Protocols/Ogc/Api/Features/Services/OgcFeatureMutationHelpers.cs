// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

internal static class OgcFeatureMutationHelpers
{
    internal readonly record struct FeatureBuildResult(
        bool IsValid,
        Feature? Feature,
        CrsDefinition? InputCrs,
        string? ErrorMessage);

    /// <summary>
    /// Resolves storage SRID, primary geometry field, and attribute validation
    /// from the V2 canonical resource. <paramref name="isUpdate"/> distinguishes
    /// create- vs update-path attribute validation (non-editable fields are
    /// rejected on update).
    /// </summary>
    public static async Task<FeatureBuildResult> TryBuildFeatureAsync(
        HttpRequest request,
        MetadataV2Resource resource,
        GeoJsonFeature requestFeature,
        ICrsRegistry crsRegistry,
        OgcFeaturesGeometryServices geometryServices,
        FeatureMutationValidator mutationValidator,
        long objectId,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        var crsResult = await OgcRequestCrsResolver.TryResolveInputCrsAsync(
            request,
            resource,
            crsRegistry,
            cancellationToken);
        if (!crsResult.IsValid)
        {
            return new FeatureBuildResult(
                false,
                null,
                null,
                crsResult.Error ?? "Invalid Content-Crs header.");
        }

        return await TryBuildFeatureAsync(
            resource,
            requestFeature,
            crsResult.Definition,
            geometryServices,
            mutationValidator,
            objectId,
            isUpdate,
            cancellationToken);
    }

    /// <summary>
    /// Builds a canonical feature from an OGC GeoJSON payload using Metadata v2
    /// resource schema and CRS metadata.
    /// </summary>
    public static async Task<FeatureBuildResult> TryBuildFeatureAsync(
        MetadataV2Resource resource,
        GeoJsonFeature requestFeature,
        CrsDefinition inputCrs,
        OgcFeaturesGeometryServices geometryServices,
        FeatureMutationValidator mutationValidator,
        long objectId,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        byte[]? geometryWkb = null;
        if (requestFeature.Geometry != null)
        {
            var storageSrid = resource.ReadSrid() ?? 4326;
            var wkbResult = await geometryServices.TryCreateWkbFromGeoJsonAsync(
                requestFeature.Geometry,
                inputCrs.Srid,
                storageSrid,
                inputCrs.AxisOrder,
                cancellationToken);
            if (!wkbResult.IsSuccess)
            {
                return new FeatureBuildResult(
                    false,
                    null,
                    inputCrs,
                    wkbResult.ErrorMessage);
            }
            geometryWkb = wkbResult.Wkb;
        }

        var geometryValidation = await mutationValidator.ValidateGeometryAsync(geometryWkb, cancellationToken);
        if (!geometryValidation.IsValid)
        {
            return new FeatureBuildResult(
                false,
                null,
                inputCrs,
                $"Invalid geometry: {geometryValidation.ErrorMessage}");
        }
        geometryWkb = geometryValidation.Geometry;

        var effectiveProperties = BuildEffectiveProperties(resource, requestFeature, out var publicIdError);
        if (publicIdError is not null)
        {
            return new FeatureBuildResult(
                false,
                null,
                inputCrs,
                publicIdError);
        }

        var attributesResult = mutationValidator.ValidateAttributes(
            resource,
            effectiveProperties,
            ValidationExtensions.AttributeValidationMode.Strict,
            isUpdate);
        if (!attributesResult.IsValid)
        {
            return new FeatureBuildResult(
                false,
                null,
                inputCrs,
                attributesResult.ErrorMessage ?? "Invalid attributes.");
        }

        var feature = Feature.Create(objectId, geometryWkb, attributesResult.Value!);
        return new FeatureBuildResult(true, feature, inputCrs, null);
    }

    private static Dictionary<string, object?> BuildEffectiveProperties(
        MetadataV2Resource resource,
        GeoJsonFeature requestFeature,
        out string? error)
    {
        error = null;
        var properties = new Dictionary<string, object?>(requestFeature.Properties, StringComparer.OrdinalIgnoreCase);
        var publicIdField = OgcFeatureIdentifierResolver.ResolveWritablePublicIdField(resource);
        if (publicIdField is null || requestFeature.Id is null)
        {
            return properties;
        }

        var topLevelId = OgcFeatureIdentifierResolver.FormatPayloadId(requestFeature.Id);
        if (topLevelId is null)
        {
            return properties;
        }

        if (properties.TryGetValue(publicIdField.Name, out var propertyIdValue))
        {
            var propertyId = OgcFeatureIdentifierResolver.FormatPayloadId(propertyIdValue);
            if (propertyId is not null &&
                !string.Equals(propertyId, topLevelId, StringComparison.Ordinal))
            {
                error = $"GeoJSON top-level id '{topLevelId}' does not match properties.{publicIdField.Name} '{propertyId}'.";
            }

            return properties;
        }

        properties[publicIdField.Name] = requestFeature.Id;
        return properties;
    }

}
