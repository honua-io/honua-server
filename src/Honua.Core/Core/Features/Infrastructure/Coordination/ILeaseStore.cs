// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Coordination;

public readonly record struct LeaseClaim(string LeaseKey, string OwnerId);

// Expiry is decided by the store's clock, never the caller's. An owner cannot acquire a lease it
// still holds; it renews it.
public interface ILeaseStore
{
    Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default);

    Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default);

    Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default);
}
