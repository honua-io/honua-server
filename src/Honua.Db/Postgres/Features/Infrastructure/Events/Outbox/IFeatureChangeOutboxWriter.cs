// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Events.Outbox;

namespace Honua.Db.Postgres.Features.Infrastructure.Events.Outbox;

internal interface IFeatureChangeOutboxWriter
{
    Task WriteOutboxRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        FeatureChangeOutboxEntry entry,
        CancellationToken cancellationToken);
}
