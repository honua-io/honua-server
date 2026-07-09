// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed class InMemoryMigrationBackupHookOutcomeStore : IMigrationBackupHookOutcomeStore
{
    private readonly object _gate = new();
    private MigrationBackupHookOutcome? _lastOutcome;

    public MigrationBackupHookOutcome? LastOutcome
    {
        get
        {
            lock (_gate)
            {
                return _lastOutcome;
            }
        }
    }

    public void Record(MigrationBackupHookOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        lock (_gate)
        {
            _lastOutcome = outcome;
        }
    }
}
