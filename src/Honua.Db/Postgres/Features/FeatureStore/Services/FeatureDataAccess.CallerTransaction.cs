// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal partial class FeatureDataAccess
{
    internal Task<FeatureEditResult> ApplyWithinTransactionAsync(
        NpgsqlTransaction transaction, (int LayerId, FeatureEditBatch Batch) edit, CancellationToken cancellationToken)
        => ApplyEditsWithinTransactionAsync(edit.LayerId, edit.Batch, transaction.Connection!, transaction, cancellationToken);
}
