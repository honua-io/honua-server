// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    Task<ConsoleJobStepsResponse?> GetStepsAsync(
        HttpContext context,
        string jobId,
        CancellationToken cancellationToken);

    Task<ConsoleJobDiagnostics?> GetDiagnosticsAsync(
        HttpContext context,
        string jobId,
        int? eventLimit,
        string? eventCursor,
        CancellationToken cancellationToken);

    Task<ConsoleJobDiagnostics?> GetDiagnosticsAsync(
        System.Security.Claims.ClaimsPrincipal user,
        string pathBase,
        string jobId,
        int? eventLimit,
        string? eventCursor,
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
