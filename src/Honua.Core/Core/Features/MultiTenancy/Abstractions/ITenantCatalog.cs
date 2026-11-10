// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Domain;

namespace Honua.Core.Features.MultiTenancy.Abstractions;

public interface ITenantCatalog
{
    Task<TenantRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> TryAddAsync(TenantRecord tenant, CancellationToken cancellationToken = default);

    Task<TenantRecord?> UpdateAsync(TenantRecord tenant, CancellationToken cancellationToken = default);
}
