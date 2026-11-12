// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Assets;

public interface IAssetProfileStore
{
    Task<AssetProfileListResult> ListAsync(AssetProfileQuery query, CancellationToken cancellationToken = default);

    Task<AssetProfile?> GetAsync(Guid profileId, CancellationToken cancellationToken = default);

    // Lets a retry return its stored receipt before any validation that the first attempt's effects could now fail.
    Task<AssetCommandResult?> FindReceiptAsync(AssetCommand command, CancellationToken cancellationToken = default);

    Task<AssetCommandResult> CreateAsync(AssetProfile profile, AssetCommand command, CancellationToken cancellationToken = default);

    // Applies the change only when the stored version equals expectedVersion; the stored version then advances by one.
    Task<AssetCommandResult> UpdateAsync(
        AssetProfile profile,
        long expectedVersion,
        AssetCommand command,
        CancellationToken cancellationToken = default);
}
