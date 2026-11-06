// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface IProgressStoreRecovery
{
    // A healthy store does no work, and the probe cost must not grow with the number of stored jobs.
    Task ProbeRecoveryAsync(CancellationToken cancellationToken = default);
}
