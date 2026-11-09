// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static partial class AdminLayerAuthoringEndpoints
{
    private sealed class LayerConfigurationValidationException(string detail) : Exception
    {
        public string Detail { get; } = detail;
    }

    private static Task<IResult> HandleGetDisplay(int layerId, HttpContext context) =>
        ReadLayerConfigurationAsync(layerId, context, resource => DisplayResponse(layerId, resource));

    private static Task<IResult> HandleSetDisplay(int layerId, LayerDisplayUpdateRequest request, HttpContext context) =>
        WriteLayerConfigurationAsync(layerId, context, (resource => ApplyDisplay(resource, request), resource => DisplayResponse(layerId, resource)));

    private static Task<IResult> HandleGetSpatial(int layerId, HttpContext context) =>
        ReadLayerConfigurationAsync(layerId, context, resource => SpatialResponse(layerId, resource));

    private static Task<IResult> HandleSetSpatial(int layerId, LayerSpatialUpdateRequest request, HttpContext context) =>
        WriteLayerConfigurationAsync(layerId, context, (resource => ApplySpatial(resource, request), resource => SpatialResponse(layerId, resource)));

    private static async Task<IResult> ReadLayerConfigurationAsync(int layerId, HttpContext context,
        Func<MetadataV2Resource, IResult> respond)
    {
        var store = context.RequestServices.GetRequiredService<IMetadataV2GraphStore>();
        var snapshot = await store.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var target = AdminLayerConfigurationTargets.Resolve(snapshot, layerId);
        if (target.IsAmbiguous)
            return ProblemDetailsHelpers.CreateAdminProblem(context, 409, target.ConflictDetail!);
        return target.Resource is null
            ? ProblemDetailsHelpers.CreateAdminProblem(context, 404, "Layer not found.")
            : respond(target.Resource);
    }

    private static async Task<IResult> WriteLayerConfigurationAsync(int layerId, HttpContext context,
        (Func<MetadataV2Resource, MetadataV2Resource> Mutate, Func<MetadataV2Resource, IResult> Respond) edit)
    {
        var store = context.RequestServices.GetRequiredService<IMetadataV2GraphStore>();
        AdminLayerConfigurationTargets.AdminLayerConfigurationTarget target;
        try
        {
            target = await AdminLayerConfigurationTargets.WriteAsync(store, layerId, edit.Mutate, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (LayerConfigurationValidationException exception)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, 400, exception.Detail);
        }
        if (target.IsAmbiguous)
            return ProblemDetailsHelpers.CreateAdminProblem(context, 409, target.ConflictDetail!);
        if (target.Resource is null)
            return ProblemDetailsHelpers.CreateAdminProblem(context, 404, "Layer not found.");
        await context.RequestServices.GetRequiredService<OutputCacheInvalidationService>()
            .InvalidateServiceCatalogAsync(null, null, context.RequestAborted).ConfigureAwait(false);
        return await ReadLayerConfigurationAsync(layerId, context, edit.Respond).ConfigureAwait(false);
    }

    private static MetadataV2Resource ApplyDisplay(MetadataV2Resource resource, LayerDisplayUpdateRequest request)
    {
        var current = resource.Display ?? new();
        var display = current with
        {
            MinScale = request.MinScale ?? current.MinScale,
            MaxScale = request.MaxScale ?? current.MaxScale,
            DisplayField = request.DisplayField is null ? current.DisplayField : request.DisplayField.Trim(),
            DefaultVisibility = request.DefaultVisibility ?? current.DefaultVisibility,
            Queryable = request.Queryable ?? current.Queryable,
            HasZ = request.HasZ ?? current.HasZ,
            HasM = request.HasM ?? current.HasM
        };
        if (display.MinScale is { } min && (!double.IsFinite(min) || min < 0) ||
            display.MaxScale is { } max && (!double.IsFinite(max) || max < 0))
            throw new LayerConfigurationValidationException("Scale denominators must be finite and nonnegative.");
        if (display.MinScale is > 0 && display.MaxScale is > 0 && display.MinScale < display.MaxScale)
            throw new LayerConfigurationValidationException("The minimum visible scale denominator must be at least the maximum visible denominator.");
        if (!string.IsNullOrWhiteSpace(display.DisplayField) && !resource.SchemaFields.Any(field =>
                string.Equals(field.Name, display.DisplayField, StringComparison.OrdinalIgnoreCase)))
            throw new LayerConfigurationValidationException("The display field must name a declared field.");
        return resource with { Display = display };
    }

    private static MetadataV2Resource ApplySpatial(MetadataV2Resource resource, LayerSpatialUpdateRequest request)
    {
        var current = resource.Spatial ?? new();
        var spatial = current with
        {
            SpatialReference = request.SpatialReference ?? current.SpatialReference,
            GeometryTypeValue = request.GeometryType is { } geometry ? new(geometry) : current.GeometryTypeValue,
            SupportedCrs = request.SupportedCrs ?? current.SupportedCrs,
            StorageCrs = request.ClearStorageCrs ? null : request.StorageCrs ?? current.StorageCrs,
            StorageCrsCoordinateEpoch = request.ClearStorageCrsCoordinateEpoch
                ? null : request.StorageCrsCoordinateEpoch ?? current.StorageCrsCoordinateEpoch
        };
        if (request.GeometryType is { } geometryType &&
            (!Enum.IsDefined(geometryType) || geometryType == MetadataV2GeometryType.Unknown))
            throw new LayerConfigurationValidationException("A supported geometry type is required.");
        if (spatial.SpatialReference is { } reference && reference.ResolveSrid() is not > 0)
            throw new LayerConfigurationValidationException("The spatial reference must resolve to a positive SRID.");
        if (spatial.SupportedCrs.Any(crs => crs is null || crs.ResolveSrid() is not > 0) ||
            spatial.StorageCrs is { } storage && storage.ResolveSrid() is not > 0)
            throw new LayerConfigurationValidationException("CRS values must resolve to a positive SRID.");
        if (spatial.StorageCrsCoordinateEpoch is { } epoch && (!double.IsFinite(epoch) || epoch < 0))
            throw new LayerConfigurationValidationException("The coordinate epoch must be finite and nonnegative.");
        return resource with { Spatial = spatial };
    }

    private static IResult DisplayResponse(int layerId, MetadataV2Resource resource)
    {
        var display = resource.Display ?? new();
        return Results.Json(ApiResponse<LayerDisplayResponse>.CreateSuccess(new()
        {
            LayerId = layerId, MinScale = display.MinScale, MaxScale = display.MaxScale,
            DefaultVisibility = display.DefaultVisibility, DisplayField = display.DisplayField,
            Queryable = display.Queryable, HasZ = display.HasZ, HasM = display.HasM
        }), LayerDisplaySpatialJsonContext.Default.ApiResponseLayerDisplayResponse);
    }

    private static IResult SpatialResponse(int layerId, MetadataV2Resource resource)
    {
        var spatial = resource.Spatial ?? new();
        return Results.Json(ApiResponse<LayerSpatialResponse>.CreateSuccess(new()
        {
            LayerId = layerId, SpatialReference = spatial.SpatialReference, GeometryType = spatial.GeometryType,
            SupportedCrs = spatial.SupportedCrs, StorageCrs = spatial.StorageCrs,
            StorageCrsCoordinateEpoch = spatial.StorageCrsCoordinateEpoch
        }), LayerDisplaySpatialJsonContext.Default.ApiResponseLayerSpatialResponse);
    }
}
