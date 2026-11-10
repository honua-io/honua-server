// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.DisasterRecovery.Domain;

namespace Honua.Core.Features.DisasterRecovery.Abstractions;

public interface IBackupStatusProvider
{
    Task<IReadOnlyList<BackupRecord>> GetBackupHistoryAsync(CancellationToken cancellationToken = default);

    Task<RecoveryReadiness> GetRecoveryReadinessAsync(CancellationToken cancellationToken = default);
}
