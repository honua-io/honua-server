// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Coordination;

namespace Honua.Server.Features.Infrastructure.Coordination;

// The job store owns the lease statements; every other lease in the system goes through them.
internal sealed class PostgresLeaseStore(PostgresExecutionJobStore jobStore) : ILeaseStore
{
    public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        => jobStore.TryAcquireLeaseAsync(claim.LeaseKey, claim.OwnerId, duration, cancellationToken);

    public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
        => jobStore.RenewLeaseAsync(claim.LeaseKey, claim.OwnerId, duration, cancellationToken);

    public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
        => jobStore.ReleaseLeaseAsync(claim.LeaseKey, claim.OwnerId, cancellationToken);
}
