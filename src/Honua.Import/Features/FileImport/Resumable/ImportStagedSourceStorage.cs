// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Import.FileImport.Resumable;

// The host binds one provider for task-bound source objects. Upload admission,
// execution authorization and materialization must resolve this same binding;
// the general file store may contain unrelated exports and artifacts. This is
// composition identity, not an assertion of capacity or execution readiness.
internal sealed class ImportStagedSourceStorage
{
    internal ImportStagedSourceStorage(ICloudFileStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Storage = storage;
    }

    internal ICloudFileStorage Storage { get; }
}
