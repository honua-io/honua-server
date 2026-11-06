// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Abstractions;

public interface ITemporalCorrectiveJobSink
{
    // The work delegate must never delete change-log history.
    Task<(string JobId, string Status)> SubmitAsync(
        string operationName,
        string serviceId,
        int layerId,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}
