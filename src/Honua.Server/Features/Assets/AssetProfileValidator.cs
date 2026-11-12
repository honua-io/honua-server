// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Assets;

// Checks a profile against the layer's published schema; customer columns are read, never rewritten.
internal sealed class AssetProfileValidator(IFormTargetMetadataResolver layerResolver, AssetFormLookup forms)
{
    private const int MaxNameLength = 200;
    private const int MaxLabelLength = 60;
    private const int ConditionLevels = 5;

    private static readonly MetadataV2FieldType[] IdentityTypes =
        [MetadataV2FieldType.String, MetadataV2FieldType.Integer, MetadataV2FieldType.BigInteger, MetadataV2FieldType.Uuid];

    private static readonly MetadataV2FieldType[] ConditionTypes =
        [MetadataV2FieldType.Integer, MetadataV2FieldType.BigInteger, MetadataV2FieldType.Double, MetadataV2FieldType.Float];

    private static readonly MetadataV2FieldType[] DateTypes = [MetadataV2FieldType.DateTime, MetadataV2FieldType.Date];

    public async Task<AssetProfileValidation> ValidateAsync(AssetProfileWriteRequest request, MetadataV2Service service, CancellationToken cancellationToken)
    {
        var shapeErrors = ShapeErrors(request).ToList();
        if (shapeErrors.Count > 0)
        {
            return new AssetProfileValidation(shapeErrors, null, null);
        }

        var target = await layerResolver.ResolveAsync(
            new FormTargetDefinition { ServiceId = request.ServiceId, LayerId = request.LayerId!.Value },
            cancellationToken).ConfigureAwait(false);
        if (target.Resource is not { } resource)
        {
            return new AssetProfileValidation([new AssetFieldError("layerId", "The layer was not found on the service.")], null, null);
        }

        var geometryType = GeometryKind(resource.ReadGeometryType());
        var mapped = MapFields(resource, request.Fields!);
        AssetFieldError?[] checks =
        [
            geometryType is null ? new AssetFieldError("layerId", "Asset layers must have point, line or polygon geometry.") : null,
            .. mapped.Errors,
            await CheckFormAsync(request.InspectionFormId, ("inspectionFormId", service), cancellationToken).ConfigureAwait(false),
            await CheckFormAsync(request.ServiceFormId, ("serviceFormId", service), cancellationToken).ConfigureAwait(false),
        ];
        return new AssetProfileValidation([.. checks.OfType<AssetFieldError>()], geometryType, mapped.Fields);
    }

    private static IEnumerable<AssetFieldError> ShapeErrors(AssetProfileWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ServiceId))
        {
            yield return new AssetFieldError("serviceId", "serviceId is required.");
        }

        if (request.LayerId is null or < 0)
        {
            yield return new AssetFieldError("layerId", "layerId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameLength)
        {
            yield return new AssetFieldError("name", $"name is required and at most {MaxNameLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Fields?.AssetId))
        {
            yield return new AssetFieldError("fields.assetId", "A stable asset ID field is required.");
        }

        if (request.ConditionLabels is not { Count: ConditionLevels } labels ||
            labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > MaxLabelLength))
        {
            yield return new AssetFieldError("conditionLabels", $"Exactly {ConditionLevels} non-empty labels of at most {MaxLabelLength} characters are required.");
        }

        if (request.State is not null && !AssetProfileState.IsValid(request.State))
        {
            yield return new AssetFieldError("state", $"state must be one of: {string.Join(", ", AssetProfileState.All)}.");
        }
    }

    private static (AssetProfileFieldMap Fields, AssetFieldError[] Errors) MapFields(MetadataV2Resource resource, AssetProfileFieldMap requested)
    {
        var assetId = MapField(resource, requested.AssetId, ("fields.assetId", IdentityTypes));
        var condition = MapField(resource, requested.Condition, ("fields.condition", ConditionTypes));
        var status = MapField(resource, requested.Status, ("fields.status", [MetadataV2FieldType.String]));
        var lastInspected = MapField(resource, requested.LastInspected, ("fields.lastInspected", DateTypes));
        var lastServiced = MapField(resource, requested.LastServiced, ("fields.lastServiced", DateTypes));
        var fields = new AssetProfileFieldMap
        {
            AssetId = assetId.Name,
            Condition = condition.Name,
            Status = status.Name,
            LastInspected = lastInspected.Name,
            LastServiced = lastServiced.Name,
        };
        return (fields, [.. new[] { assetId.Error, condition.Error, status.Error, lastInspected.Error, lastServiced.Error }.OfType<AssetFieldError>()]);
    }

    // Returns the layer's own spelling of the field so later queries match the published schema exactly.
    private static (string? Name, AssetFieldError? Error) MapField(
        MetadataV2Resource resource,
        string? requested,
        (string Path, MetadataV2FieldType[] Types) rule)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return (null, null);
        }

        var field = resource.SchemaFields.FirstOrDefault(f => string.Equals(f.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return (null, new AssetFieldError(rule.Path, $"Field '{requested}' does not exist on the layer."));
        }

        return rule.Types.Contains(field.Type)
            ? (field.Name, null)
            : (null, new AssetFieldError(rule.Path, $"Field '{field.Name}' has type {field.Type}, which cannot hold this value."));
    }

    private async Task<AssetFieldError?> CheckFormAsync(string? formId, (string Path, MetadataV2Service Service) target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formId))
        {
            return null;
        }

        return await forms.FindPublishedAsync(formId, target.Service, cancellationToken).ConfigureAwait(false) is null
            ? new AssetFieldError(target.Path, $"Form '{formId}' has no published version on this service.")
            : null;
    }

    private static string? GeometryKind(MetadataV2GeometryType type) => type switch
    {
        MetadataV2GeometryType.Point or MetadataV2GeometryType.MultiPoint => AssetGeometryKind.Point,
        MetadataV2GeometryType.LineString or MetadataV2GeometryType.MultiLineString => AssetGeometryKind.Line,
        MetadataV2GeometryType.Polygon or MetadataV2GeometryType.MultiPolygon => AssetGeometryKind.Polygon,
        _ => null,
    };
}

internal sealed record AssetProfileValidation(
    IReadOnlyList<AssetFieldError> Errors,
    string? GeometryType,
    AssetProfileFieldMap? Fields);
