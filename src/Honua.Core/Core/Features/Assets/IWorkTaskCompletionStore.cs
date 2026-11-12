// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Assets;

// Completion writes the task change, its history event, the inspection or service record, the asset summary and the
// command receipt in one transaction: a failure at any write leaves none of them behind.
public interface IWorkTaskCompletionStore
{
    Task<AssetCommandResult> CompleteAsync(WorkTaskCompletionCommand completion, CancellationToken cancellationToken = default);
}
