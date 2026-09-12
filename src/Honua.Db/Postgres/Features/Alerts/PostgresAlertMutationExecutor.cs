// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Db.Postgres.Features.Alerts;

internal sealed class PostgresAlertMutationExecutor(IAdoNetDatabaseConnectionProvider provider) : IAlertMutationExecutor
{
    public ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> mutation, Func<T, bool> shouldCommit,
        CancellationToken cancellationToken = default) =>
        Infrastructure.PostgresMutationTransaction.ExecuteAsync(provider, mutation, shouldCommit, cancellationToken);
}
