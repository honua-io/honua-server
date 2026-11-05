// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Abstractions;

internal readonly record struct UploadQueueSnapshot(
    int QueueDepth,
    int MaxQueueDepth,
    int ActiveUploads,
    int MaxConcurrentUploads);

internal interface IUploadQueueMetricsProvider
{
    UploadQueueSnapshot GetQueueSnapshot();
}
