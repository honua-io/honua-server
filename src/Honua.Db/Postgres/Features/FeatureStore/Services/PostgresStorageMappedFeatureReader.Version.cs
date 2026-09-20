// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader : IBranchVersioningFeatureReader
{
    private bool HasManagedVersionMapping =>
        // The managed writer stores deltas in this fixed column shape. An arbitrary
        // source table (or a column-per-field mapping) cannot consume those deltas.
        _mapping.SupportsManagedWrites
        && _mapping.TableName == FeatureStorageMapping.ManagedFeaturesTableName
        && _mapping.PrimaryKeyColumn == "objectid"
        && _mapping.AttributesColumn == "attributes"
        && _mapping.LayerDiscriminatorColumn == "layer_id"
        && _mapping.LayerDiscriminatorValue.HasValue
        && _mapping.GeometryColumn is null or "geometry";

    private const string UnsupportedVersionMappingMessage =
        "Branch-versioned reads require the managed shared feature-table mapping. " +
        "External source mappings do not support branch overlays.";

    private void RequireManagedVersionMapping()
    {
        if (!HasManagedVersionMapping)
        {
            throw new NotSupportedException(UnsupportedVersionMappingMessage);
        }
    }

    public async Task<bool> SupportsBranchVersioningAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await GetUnsupportedBranchReadReasonAsync().ConfigureAwait(false) is null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or CryptographicException)
        {
            // Metadata must not advertise a distinct connection whose credentials cannot
            // be resolved. Actual reads preserve the existing diagnostic exception below.
            return false;
        }
    }

    private async Task<string?> GetUnsupportedBranchReadReasonAsync()
    {
        if (!HasManagedVersionMapping)
        {
            return UnsupportedVersionMappingMessage;
        }
        var boundConnection = await ResolveBoundConnectionStringAsync().ConfigureAwait(false);
        if (boundConnection is not null
            && !new NpgsqlConnectionStringBuilder(boundConnection).EquivalentTo(
                new NpgsqlConnectionStringBuilder(_connectionProvider.GetConnectionString())))
        {
            return "Branch-versioned reads do not support an external database connection. " +
                "The managed feature table and version deltas must use the managed connection.";
        }
        return null;
    }

    private async Task ValidateVersionedReadAsync(FeatureQuery query)
    {
        if (query.VersionContext is not { IsDefault: false })
        {
            return;
        }

        var unsupportedReason = await GetUnsupportedBranchReadReasonAsync().ConfigureAwait(false);
        if (unsupportedReason != null)
        {
            throw new NotSupportedException(unsupportedReason);
        }
    }

    private string BuildFeatureSource(FeatureQuery query, SqlBuilder sql)
    {
        if (query.VersionContext is not { IsDefault: false } version)
        {
            // Keep DEFAULT SQL and parameter ordering byte-for-byte unchanged.
            return _qualifiedTableName;
        }

        RequireManagedVersionMapping();
        var versionId = version.VersionId
            ?? throw new InvalidOperationException("Non-default version context is missing a version id.");
        var versionParameter = sql.AddParameter(versionId);
        var layerParameter = sql.AddParameter(_mapping.LayerDiscriminatorValue!.Value);

        // Preserve the mapping's column shape and apply all normal outer predicates,
        // permanent filters, RLS and masks to the resulting effective feature rows.
        // Updates/deletes shadow DEFAULT; additions and updates come from the overlay.
        return $"(SELECT b.objectid, b.layer_id, b.geometry, b.attributes FROM {_qualifiedTableName} b " +
               $"WHERE b.layer_id = {layerParameter} AND NOT EXISTS " +
               "(SELECT 1 FROM honua.version_edits shadow " +
               $"WHERE shadow.version_id = {versionParameter} AND shadow.layer_id = b.layer_id " +
               "AND shadow.objectid = b.objectid) UNION ALL " +
               "SELECT delta.objectid, delta.layer_id, delta.geometry, delta.attributes " +
               "FROM honua.version_edits delta " +
               $"WHERE delta.version_id = {versionParameter} AND delta.layer_id = {layerParameter} " +
               "AND delta.operation <> 3) AS branch_features";
    }
}
