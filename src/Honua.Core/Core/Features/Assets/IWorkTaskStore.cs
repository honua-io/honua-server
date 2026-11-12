// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Assets;

// Every accepted mutation advances the task version, appends one history event and records the command receipt in one transaction.
public interface IWorkTaskStore
{
    Task<WorkTaskListResult> ListAsync(WorkTaskQuery query, CancellationToken cancellationToken = default);

    Task<WorkTask?> GetAsync(Guid taskId, CancellationToken cancellationToken = default);

    // Lets a retry return its stored receipt before any validation that the first attempt's effects could now fail.
    Task<AssetCommandResult?> FindReceiptAsync(AssetCommand command, CancellationToken cancellationToken = default);

    Task<AssetCommandResult> CreateAsync(WorkTaskDraft draft, AssetCommand command, CancellationToken cancellationToken = default);

    Task<AssetCommandResult> TransitionAsync(WorkTaskTransitionCommand transition, CancellationToken cancellationToken = default);

    Task<WorkTaskHistoryResult> ListHistoryAsync(WorkTaskHistoryQuery query, CancellationToken cancellationToken = default);
}
