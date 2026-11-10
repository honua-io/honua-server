// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Abstractions;

public interface IAlertMutationExecutor
{
    ValueTask<T> ExecuteAsync<T>(
        Func<ValueTask<T>> mutation,
        Func<T, bool> shouldCommit,
        CancellationToken cancellationToken = default);
}
