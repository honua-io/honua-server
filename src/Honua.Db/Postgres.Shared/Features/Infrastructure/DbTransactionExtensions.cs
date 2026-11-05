// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;

namespace Honua.Db.Postgres.Features.Infrastructure;

// Commit with CancellationToken.None after a pre-check, because a token that fires after the server accepts COMMIT still throws and a retry then inserts a duplicate.
internal static class DbTransactionExtensions
{
    public static Task CommitSafelyAsync(this DbTransaction transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return transaction.CommitAsync(CancellationToken.None);
    }
}
