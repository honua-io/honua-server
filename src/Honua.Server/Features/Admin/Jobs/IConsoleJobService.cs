// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Jobs;

internal interface IConsoleJobService
{
    Task<ConsoleJobListResponse> ListAsync(
        HttpContext context,
        ConsoleJobQueryRequest request,
        CancellationToken cancellationToken);

    Task<ConsoleJobDetail?> GetDetailAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken);

    Task<ConsoleJobLogPageResponse?> GetLogsAsync(
        HttpContext context,
        string jobId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken);

    Task<ConsoleJobArtifactPageResponse?> GetArtifactsAsync(
        HttpContext context,
        string jobId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken);

    Task<ConsoleJobActionsResponse?> GetActionsAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken);

    Task<ConsoleJobControlResponse?> CancelAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken);

    Task<ConsoleJobControlResponse?> RetryAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken);
}
