// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Server.Features.Assets;

// Activation inspects customer data without changing its columns or values.
internal sealed class AssetProfileActivationValidator(IFeatureReader features)
{
    public async Task<IReadOnlyList<AssetFieldError>> ValidateAsync(
        FormTargetMetadataResolution target, AssetProfileFieldMap fields, CancellationToken cancellationToken)
    {
        var errors = WritableFieldErrors(target, fields).ToList();
        if (target.StorageLayerId is not { } layerId)
        {
            errors.Add(new AssetFieldError("layerId", "The layer has no queryable storage binding."));
            return errors;
        }

        var identity = fields.AssetId!;
        var quotedIdentity = $"\"{identity.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        var nullCount = await features.CountAsync(layerId,
            new FeatureQuery { Where = $"{quotedIdentity} IS NULL", IncludeNullGeometry = true }, cancellationToken).ConfigureAwait(false);
        if (nullCount > 0)
        {
            errors.Add(new AssetFieldError("fields.assetId", "Every asset must have a non-null stable ID before activation."));
        }

        var duplicates = await features.QueryStatisticsAsync(layerId, DuplicateQuery(identity), cancellationToken).ConfigureAwait(false);
        if (!duplicates.IsEmpty)
        {
            errors.Add(new AssetFieldError("fields.assetId", "Asset IDs must be unique before activation."));
        }

        return errors;
    }

    private static FeatureQuery DuplicateQuery(string identity) => new()
    {
        IncludeNullGeometry = true,
        GroupByFields = [identity],
        OutStatistics = [new StatisticDefinition
        {
            StatisticType = StatisticType.Count, OnStatisticField = identity, OutStatisticFieldName = "asset_count",
        }],
        Having = [new HavingCondition
        {
            StatisticType = StatisticType.Count, OnStatisticField = identity,
            Operator = HavingComparisonOperator.GreaterThan, Value = 1,
        }],
        Limit = 1,
    };

    private static IEnumerable<AssetFieldError> WritableFieldErrors(FormTargetMetadataResolution target, AssetProfileFieldMap fields)
    {
        (string Path, string? Name)[] mappings =
        [
            ("fields.condition", fields.Condition), ("fields.status", fields.Status),
            ("fields.lastInspected", fields.LastInspected), ("fields.lastServiced", fields.LastServiced),
        ];
        foreach (var (path, name) in mappings)
        {
            if (name is not null && target.Resource!.SchemaFields.Any(field => field.Name == name && !field.Editable))
            {
                yield return new AssetFieldError(path, $"Field '{name}' must be editable before activation.");
            }
        }
    }
}
