// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed class MigrationBackupHookState
{
    private DatabaseMigrationBackupHookResult? _latest;

    public DatabaseMigrationBackupHookResult? Latest => Volatile.Read(ref _latest);

    public void Record(DatabaseMigrationBackupHookResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Volatile.Write(ref _latest, result);
    }
}
