// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;

namespace Honua.Import.FileImport.Durable;

internal static class FileImportJobSubmission
{
    public static (ImportProgress Progress, ImportLimits Limits) CreateQueuedProgress(
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

        var progress = ImportProgress.CreateInitial(
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
        return (progress, importService.Limits);
    }
}
