// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OgcFeatures.Models;

namespace Honua.Server.Features.OgcFeatures.Services;

internal static class OgcFeatureMutationHelpers
{
    internal readonly record struct FeatureBuildResult(
        bool IsValid,
        Feature? Feature,
        CrsDefinition? InputCrs,
        string? ErrorMessage);

    public static async Task<FeatureBuildResult> TryBuildFeatureAsync(
        HttpRequest request,
        LayerDefinition layer,
        GeoJsonFeature requestFeature,
        ICrsRegistry crsRegistry,
        OgcFeaturesGeometryServices geometryServices,
        FeatureMutationValidator mutationValidator,
        long objectId,
        CancellationToken cancellationToken)
    {
        var crsResult = await OgcRequestCrsResolver.TryResolveInputCrsAsync(
            request,
            layer,
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
            layer,
            requestFeature,
            crsResult.Definition,
            geometryServices,
            mutationValidator,
            objectId,
            cancellationToken);
    }

    public static async Task<FeatureBuildResult> TryBuildFeatureAsync(
        LayerDefinition layer,
        GeoJsonFeature requestFeature,
        CrsDefinition inputCrs,
        OgcFeaturesGeometryServices geometryServices,
        FeatureMutationValidator mutationValidator,
        long objectId,
        CancellationToken cancellationToken)
    {
        byte[]? geometryWkb = null;
        if (requestFeature.Geometry != null)
        {
            var wkbResult = await geometryServices.TryCreateWkbFromGeoJsonAsync(
                requestFeature.Geometry,
                inputCrs.Srid,
                layer.SpatialReference.ToSrid(),
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

        var attributesResult = mutationValidator.ValidateAttributes(
            layer,
            requestFeature.Properties,
            ValidationExtensions.AttributeValidationMode.Strict);
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
}
