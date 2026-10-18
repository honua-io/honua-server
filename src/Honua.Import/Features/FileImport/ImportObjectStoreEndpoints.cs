// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Microsoft.Extensions.Options;

namespace Honua.Import.FileImport;

internal static partial class ImportEndpoints
{
    private static async Task HandleImportFileFromObjectStore(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await HandlePostMethodNotAllowed(context);
            return;
        }

        CancellationToken cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        if (!await AuthorizeImportMutationAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var request = await JsonSerializer.DeserializeAsync(
            context.Request.Body,
            ImportJsonContext.Default.ImportObjectStoreImportRequest,
            cancellationToken);

        if (request == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SourceUri))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "sourceUri is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.TableName))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Table name is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!ImportValidationHelpers.IsValidTableName(request.TableName))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Invalid table name. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.TargetSchema) &&
            !ImportValidationHelpers.IsValidSchemaName(request.TargetSchema))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Invalid target schema. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (!ImportValidationHelpers.IsPermittedTargetSchema(request.TargetSchema, context.RequestServices))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                ImportTargetSchemaPolicy.NotOperationalSchemaMessage,
                StatusCodes.Status400BadRequest);
            return;
        }

        var importService = context.RequestServices.GetRequiredService<IFileImportService>();
        var options = context.RequestServices.GetRequiredService<IOptions<ImportObjectStoreOptions>>().Value;
        var admission = await ImportObjectStoreCoordinator.EvaluateAsync(
            new ImportObjectStoreCoordinator.Command
            {
                SourceUri = request.SourceUri,
                AssumeRoleArn = request.AssumeRoleArn,
                TableName = request.TableName,
                TargetSchema = request.TargetSchema,
                SourceSrid = request.SourceSrid,
                TargetSrid = request.TargetSrid,
                OverwriteExisting = request.OverwriteExisting,
                OperationInstanceId = ReadLineageHeader(context, "X-Honua-Operation-Instance-Id"),
                CorrelationId = context.TraceIdentifier,
                AuditId = ReadLineageHeader(context, "X-Honua-Audit-Id"),
                ProposalId = ReadLineageHeader(context, "X-Honua-Proposal-Id"),
            },
            options,
            importService.Limits,
            context.RequestServices.GetService<IImportObjectStoreReader>(),
            CanAcceptBackgroundImports(context.RequestServices),
            fileName => importService.DetectFormat(fileName) is not null,
            cancellationToken).ConfigureAwait(false);

        if (admission.Problem is not null)
        {
            await ImportObjectStoreAudit.TryRecordAsync(
                context,
                admission.AuditSourceUri,
                admission.ETag,
                admission.SizeBytes,
                admission.AuditRoleArn,
                jobId: null,
                admission.Outcome,
                cancellationToken).ConfigureAwait(false);
            await WriteObjectStoreProblemAsync(context, admission.Problem).ConfigureAwait(false);
            return;
        }

        if (!await AuthorizeImportMutationAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var importRequest = admission.Request! with { Ownership = ImportOperationAuthorization.Capture(context) };
        try
        {
            if (admission.Background)
            {
                var jobService = context.RequestServices.GetRequiredService<IImportJobService>();
                var jobId = await jobService.QueueImportAsync(importRequest, admission.SizeBytes ?? 0, cancellationToken)
                    .ConfigureAwait(false);
                await ImportObjectStoreAudit.TryRecordAsync(
                    context,
                    admission.AuditSourceUri,
                    admission.ETag,
                    admission.SizeBytes,
                    admission.AuditRoleArn,
                    jobId,
                    ImportObjectStoreAudit.Queued,
                    cancellationToken).ConfigureAwait(false);
                var response = new BackgroundImportResponse
                {
                    JobId = jobId,
                    Message = "File queued for background processing",
                    StatusUrl = $"/api/v1/admin/import/jobs/{jobId}",
                    CancelUrl = $"/api/v1/admin/import/jobs/{jobId}/cancel",
                    OperationInstanceId = importRequest.OperationInstanceId,
                    CorrelationId = importRequest.CorrelationId,
                    AuditId = importRequest.AuditId,
                    ProposalId = importRequest.ProposalId,
                };
                await Results.Json(
                    response,
                    ImportJsonContext.Default.BackgroundImportResponse,
                    statusCode: StatusCodes.Status202Accepted).ExecuteAsync(context);
                return;
            }

            var importResult = await importService.ImportFileAsync(importRequest, cancellationToken).ConfigureAwait(false);
            importResult = importResult with
            {
                SourceKind = importRequest.SourceKind,
                SourceUrl = importRequest.SourceUrl,
            };
            if (importResult.Success)
            {
                var snapshotSchema = string.IsNullOrWhiteSpace(importResult.Schema)
                    ? importRequest.TargetSchema
                    : importResult.Schema;
                var snapshotTable = string.IsNullOrWhiteSpace(importResult.PhysicalTableName)
                    ? ResolvePhysicalImportedTableName(importRequest.TableName)
                    : importResult.PhysicalTableName;
                await TryRefreshPublishedSnapshotAsync(context, snapshotSchema, snapshotTable, cancellationToken)
                    .ConfigureAwait(false);
            }

            await ImportObjectStoreAudit.TryRecordAsync(
                context,
                admission.AuditSourceUri,
                admission.ETag,
                admission.SizeBytes,
                admission.AuditRoleArn,
                jobId: null,
                importResult.Success ? ImportObjectStoreAudit.Succeeded : ImportObjectStoreAudit.Failed,
                cancellationToken).ConfigureAwait(false);
            await Results.Json(
                importResult,
                ImportJsonContext.Default.ImportResult,
                statusCode: !importResult.Success && importResult.ErrorCode == ImportTableOwnershipException.Code
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status200OK).ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ImportJobQueueFullException)
        {
            await ImportObjectStoreAudit.TryRecordAsync(
                context,
                admission.AuditSourceUri,
                admission.ETag,
                admission.SizeBytes,
                admission.AuditRoleArn,
                jobId: null,
                ImportObjectStoreAudit.QueueFull,
                cancellationToken).ConfigureAwait(false);
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Import job queue is full. Retry later.",
                StatusCodes.Status429TooManyRequests);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, importRequest.TableName, ex);
            await ImportObjectStoreAudit.TryRecordAsync(
                context,
                admission.AuditSourceUri,
                admission.ETag,
                admission.SizeBytes,
                admission.AuditRoleArn,
                jobId: null,
                ImportObjectStoreAudit.Failed,
                cancellationToken).ConfigureAwait(false);
            await AdminResponseWriter.WriteErrorAsync(context, "Import failed: invalid or unsupported file format", StatusCodes.Status400BadRequest);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<ImportEndpointsLog>>();
            Log.ImportFailed(logger, importRequest.TableName, ex);
            await ImportObjectStoreAudit.TryRecordAsync(
                context,
                admission.AuditSourceUri,
                admission.ETag,
                admission.SizeBytes,
                admission.AuditRoleArn,
                jobId: null,
                ex.Message == BackgroundImportUnavailableMessage
                    ? ImportObjectStoreAudit.Unavailable
                    : ImportObjectStoreAudit.Failed,
                cancellationToken).ConfigureAwait(false);
            if (ex.Message == BackgroundImportUnavailableMessage)
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context,
                    BackgroundImportUnavailableMessage,
                    StatusCodes.Status503ServiceUnavailable);
                return;
            }

            await AdminResponseWriter.WriteErrorAsync(context, "Import failed", StatusCodes.Status500InternalServerError);
        }
        finally
        {
            if (admission.DisposeStream && importRequest.FileStream is not null)
            {
                await importRequest.FileStream.DisposeAsync().ConfigureAwait(false);
            }

            if (admission.DeleteLocalFile)
            {
                MultipartParsingHelpers.TryDeleteFile(importRequest.LocalFilePath);
            }
        }
    }

    internal static void ReportAuditFailure(HttpContext context, Exception exception)
    {
        var logger = context.RequestServices.GetService<ILogger<ImportEndpointsLog>>();
        if (logger is not null)
        {
            Log.AuditRecordFailed(logger, exception);
        }
    }

    private static Task WriteObjectStoreProblemAsync(HttpContext context, ImportObjectStoreProblem problem)
    {
        if (problem.Code is null)
        {
            return AdminResponseWriter.WriteErrorAsync(context, problem.Detail, problem.StatusCode);
        }

        return ProblemDetailsHelpers.CreateProblem(
            context,
            problem.Type,
            problem.StatusCode,
            problem.Title,
            problem.Detail,
            problem.Code).ExecuteAsync(context);
    }
}

internal sealed record ImportObjectStoreImportRequest
{
    public required string SourceUri { get; init; }

    public string? AssumeRoleArn { get; init; }

    public required string TableName { get; init; }

    public string? TargetSchema { get; init; }

    public int? SourceSrid { get; init; }

    // A setter preserves the initializer when .NET 10 source-generated JSON omits this field.
    public int TargetSrid { get; set; } = 4326;

    public bool OverwriteExisting { get; init; }
}
