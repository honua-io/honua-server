// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;

namespace Honua.Import.FileImport.Durable;

/// <summary>
/// Accepting-host half of a durable file import (honua-server#5916): validates the request and
/// its format exactly as the in-process runner does before it queues, and builds the initial
/// import job document the worker later advances.
/// </summary>
internal static class FileImportJobSubmission
{
    /// <summary>
    /// Validates <paramref name="request"/> and returns the queued job document.
    /// </summary>
    /// <exception cref="NotSupportedException">The format is unknown or gated off.</exception>
    public static ImportProgress CreateQueuedProgress(
        IServiceScopeFactory scopeFactory,
        ImportRequest request,
        long fileSizeBytes,
        string jobId,
        string runner,
        string? instanceId)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        using var scope = scopeFactory.CreateScope();
        var importService = scope.ServiceProvider.GetRequiredService<IFileImportService>();
        var format = ImportFormatDispatchGuard.RequireGatedFormat(
            importService.DetectFormat(request.FileName), request.FileName);

        return ImportProgress.CreateInitial(
            jobId,
            request.TableName,
            format,
            fileSizeBytes,
            request.FileName,
            request.SourceKind,
            request.SourceUrl,
            request.CloudFileId,
            request.UploadId,
            operationInstanceId: request.OperationInstanceId,
            correlationId: request.CorrelationId,
            auditId: request.AuditId,
            proposalId: request.ProposalId) with
        {
            Runner = runner,
            InstanceId = instanceId,
        };
    }
}
