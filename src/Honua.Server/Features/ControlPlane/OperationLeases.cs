// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;

namespace Honua.ControlPlane;

internal sealed class OperationLeases(ILeaseStore leases, string keyPrefix)
{
    public Task<bool> TryAcquireAsync(LeaseClaim operation, TimeSpan duration, CancellationToken cancellationToken)
        => leases.TryAcquireAsync(Prefixed(operation), duration, cancellationToken);

    public Task<bool> RenewAsync(LeaseClaim operation, TimeSpan duration, CancellationToken cancellationToken)
        => leases.RenewAsync(Prefixed(operation), duration, cancellationToken);

    public Task ReleaseAsync(LeaseClaim operation, CancellationToken cancellationToken)
        => leases.ReleaseAsync(Prefixed(operation), cancellationToken);

    private LeaseClaim Prefixed(LeaseClaim operation) => operation with { LeaseKey = keyPrefix + operation.LeaseKey };
}
