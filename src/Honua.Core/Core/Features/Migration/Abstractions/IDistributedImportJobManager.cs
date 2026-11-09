// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

public interface IDistributedImportJobManager
{
    IDistributedJobQueueService JobQueue { get; }

    IDistributedLeaderElection LeaderElection { get; }

    IDistributedProgressStore<GeoservicesImportProgress> ProgressStore { get; }

    IDistributedProgressStore<GeoservicesImportRequest> RequestStore { get; }
}
