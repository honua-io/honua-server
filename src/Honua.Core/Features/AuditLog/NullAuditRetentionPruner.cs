// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Export;

namespace Honua.Core.Features.AuditLog;

public sealed class NullAuditRetentionPruner : IAuditRetentionPruner
{
    public static readonly NullAuditRetentionPruner Instance = new();

    public Task<int> PruneAsync(AuditRetentionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Task.FromResult(0);
    }
}
