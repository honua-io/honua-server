// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>Owns the atomic persistence boundary for an alert mutation and its audit receipt.</summary>
public interface IAlertMutationExecutor
{
    /// <summary>Commits successful results together; otherwise rolls back every enlisted write.</summary>
    ValueTask<T> ExecuteAsync<T>(
        Func<ValueTask<T>> mutation,
        Func<T, bool> shouldCommit,
        CancellationToken cancellationToken = default);
}
