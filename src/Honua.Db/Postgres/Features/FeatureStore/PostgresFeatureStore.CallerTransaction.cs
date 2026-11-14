// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore;

internal sealed partial class PostgresFeatureStoreRefactored
{
    internal async Task<FeatureEditResult> ApplyWithinTransactionAsync(
        NpgsqlTransaction transaction, (int LayerId, FeatureEditBatch Batch) edit, CancellationToken cancellationToken)
    {
        var batch = await PrepareEditBatchAsync(edit.LayerId, edit.Batch, cancellationToken).ConfigureAwait(false);
        if (_dataAccess is not FeatureDataAccess postgres)
        {
            throw new NotSupportedException("The feature store does not support this transaction.");
        }

        return await postgres.ApplyWithinTransactionAsync(transaction, (edit.LayerId, batch), cancellationToken).ConfigureAwait(false);
    }
}
