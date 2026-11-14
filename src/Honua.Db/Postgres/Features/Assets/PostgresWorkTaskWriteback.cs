// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Assets;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Assets;

internal sealed class PostgresWorkTaskWriteback(PostgresFeatureStoreRefactored features)
{
    public static async Task<AssetCommandResult?> LockProfileAsync(NpgsqlTransaction transaction,
        (WorkTask Task, WorkTaskCompletionCommand Completion) change, CancellationToken cancellationToken)
    {
        var (task, completion) = change;
        if (task.ProfileId is not Guid profileId)
        {
            return null;
        }

        var table = SchemaSearchPath.QualifyTable("asset_profiles", "honua");
        await using var command = new NpgsqlCommand($"SELECT version, state FROM {table} WHERE profile_id = @id FOR SHARE", transaction.Connection, transaction);
        command.Parameters.AddWithValue("id", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return AssetCommandResult.NotFound;
        }

        return completion.AssetWriteback?.ProfileVersion == reader.GetInt64(0) && reader.GetString(1) == AssetProfileState.Active
            ? null
            : AssetCommandResult.VersionConflict(completion.Command.CommandId, reader.GetInt64(0), "profile");
    }

    public async Task<AssetCommandResult?> ApplyAsync(NpgsqlTransaction transaction, WorkTaskAssetWriteback? plan, CancellationToken cancellationToken)
    {
        if (plan?.Batch is not { } batch)
        {
            return null;
        }

        var result = await features.ApplyWithinTransactionAsync(transaction, (plan.StorageLayerId, batch), cancellationToken).ConfigureAwait(false);
        return !result.WasRolledBack && result.UpdatedCount == 1
            ? null
            : new AssetCommandResult(AssetCommandStatus.Rejected, null, "The mapped asset changed or the update was refused; reload and retry.") { Field = "assetId" };
    }
}
