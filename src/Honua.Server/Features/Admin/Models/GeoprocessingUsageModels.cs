// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class GeoprocessingUsageRankingResponse
{
    public required int Count { get; init; }

    public required GeoprocessingToolUsageRankEntry[] Tools { get; init; }
}

public sealed class GeoprocessingToolUsageRankEntry
{
    public required int Rank { get; init; }

    public required string ProcessId { get; init; }

    public required long InvocationCount { get; init; }

    public required long SuccessCount { get; init; }

    public required long FailureCount { get; init; }

    public required DateTimeOffset LastInvokedAt { get; init; }
}
