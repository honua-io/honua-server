// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Abstractions;

// Serializes conditional saves of one style write target across every node. A save checks its
// precondition only after the previous holder's save, including its catalog mirror, has finished.
public interface IStyleSaveLock
{
    // Waits for the target; null when it stays held past the store's wait limit. Dispose releases it.
    Task<IAsyncDisposable?> TryAcquireAsync(string targetKey, CancellationToken cancellationToken = default);
}
