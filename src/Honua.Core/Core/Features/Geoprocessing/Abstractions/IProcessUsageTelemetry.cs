// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Abstractions;

public interface IProcessUsageTelemetry
{
    void RecordInvocation(string processId, bool succeeded);

    IReadOnlyList<ProcessUsageRanking> GetRanking(int? limit = null);
}

public sealed record ProcessUsageRanking(
    string ProcessId,
    long InvocationCount,
    long SuccessCount,
    long FailureCount,
    DateTimeOffset LastInvokedAt);
