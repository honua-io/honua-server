// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>Verifies that a bound reader identifies the primary writer's managed storage.</summary>
public interface IManagedFeatureWriteSupport
{
    /// <summary>Checks the physical relation, connection, column mapping and managed layer handle.</summary>
    /// <param name="storageLayerId">The layer handle supplied to the primary writer.</param>
    /// <param name="cancellationToken">Token used to cancel storage identity resolution.</param>
    /// <returns>True only when primary writes target this reader's bound storage.</returns>
    Task<bool> SupportsManagedWritesAsync(int storageLayerId, CancellationToken cancellationToken = default);
}
