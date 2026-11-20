// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.Security;

namespace Honua.Import.FileImport;

internal static class ImportObjectStoreCoordinator
{
    internal const string ReaderUnavailableDetail = "Object store import is not available on this host.";

    internal const string UnsupportedExtensionDetail = "The object does not have a supported import extension.";

    internal sealed record Command
    {
        public required ImportObjectLocation Location { get; init; }

        public required string TableName { get; init; }

        public string? TargetSchema { get; init; }

        public int? SourceSrid { get; init; }

        public int? DatumTransformationWkid { get; init; }

        public string? DatumTransformationName { get; init; }

        public double? CoordinateEpoch { get; init; }

        public int? TargetSrid { get; init; }

        public bool OverwriteExisting { get; init; }

        public string? OperationInstanceId { get; init; }

        public string? CorrelationId { get; init; }

        public string? AuditId { get; init; }

        public string? ProposalId { get; init; }
    }

    internal sealed record Admission
    {
        public ImportObjectStoreProblem? Problem { get; init; }

        public ImportRequest? Request { get; init; }

        public bool Background { get; init; }

        public long? SizeBytes { get; init; }

        public string? ETag { get; init; }

        public string? AuditSourceUri { get; init; }

        public string? AuditConnectionId { get; init; }

        public required string Outcome { get; init; }

        public bool DisposeStream { get; init; }

        public bool DeleteLocalFile { get; init; }

        public ImportObjectStoreAudit.Entry ToAudit(string outcome, string? jobId = null) => new()
        {
            SourceUri = AuditSourceUri,
            ETag = ETag,
            SizeBytes = SizeBytes,
            ConnectionId = AuditConnectionId,
            JobId = jobId,
            Outcome = outcome,
        };
    }

    internal sealed record Host
    {
        public required ImportLimits Limits { get; init; }

        public IImportObjectStoreReader? Reader { get; init; }

        public bool CanAcceptBackground { get; init; }

        public required Func<string, bool> FormatSupported { get; init; }
    }

    public static async Task<Admission> EvaluateAsync(
        Command command,
        Host host,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(host);
        var limits = host.Limits;
        var reader = host.Reader;
        var admitted = command.Location;
        if (reader is null)
        {
            return Denied(admitted, Unavailable(ReaderUnavailableDetail), ImportObjectStoreAudit.Unavailable);
        }

        ImportObjectHead head;
        try
        {
            head = await reader.HeadAsync(admitted, cancellationToken).ConfigureAwait(false);
        }
        catch (ImportObjectStoreException ex)
        {
            return Denied(admitted, ProblemFor(ex), OutcomeFor(ex));
        }

        if (head.SizeBytes > limits.MaxObjectStoreImportBytes)
        {
            return Denied(
                admitted,
                new ImportObjectStoreProblem
                {
                    StatusCode = StatusCodes.Status413PayloadTooLarge,
                    Title = "Object store source is too large",
                    Detail =
                        $"The object is {head.SizeBytes} bytes, which exceeds MaxObjectStoreImportBytes ({limits.MaxObjectStoreImportBytes}).",
                    Code = ImportObjectStoreProblems.TooLarge,
                },
                ImportObjectStoreAudit.TooLarge,
                head);
        }

        var fileName = ImportObjectStoreFormats.FileNameFor(admitted.Key, head.ContentType);
        if (!host.FormatSupported(fileName))
        {
            return Denied(
                admitted,
                new ImportObjectStoreProblem
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                    Title = "Unsupported import format",
                    Detail = UnsupportedExtensionDetail,
                },
                ImportObjectStoreAudit.Failed,
                head);
        }

        var background = ImportObjectStoreFormats.IsArchive(fileName)
            || head.SizeBytes > limits.BackgroundJobThresholdBytes;
        if (background)
        {
            if (!host.CanAcceptBackground)
            {
                return Denied(
                    admitted,
                    Unavailable(ImportEndpoints.BackgroundImportUnavailableMessage),
                    ImportObjectStoreAudit.Unavailable,
                    head);
            }

            return new Admission
            {
                Background = true,
                Request = BaseRequest(command, fileName, admitted, head),
                SizeBytes = head.SizeBytes,
                ETag = head.ETag,
                AuditSourceUri = admitted.SourceUri,
                AuditConnectionId = admitted.Connection.Id.ToString(),
                Outcome = ImportObjectStoreAudit.Queued,
            };
        }

        Stream stream;
        try
        {
            stream = await reader.OpenReadAsync(admitted, head.ETag, cancellationToken).ConfigureAwait(false);
        }
        catch (ImportObjectStoreException ex)
        {
            return Denied(admitted, ProblemFor(ex), OutcomeFor(ex), head);
        }

        if (!ImportObjectStoreFormats.NeedsRandomAccess(fileName))
        {
            return new Admission
            {
                Request = ClearObjectStore(BaseRequest(command, fileName, admitted, head)) with { FileStream = stream },
                SizeBytes = head.SizeBytes,
                ETag = head.ETag,
                AuditSourceUri = admitted.SourceUri,
                AuditConnectionId = admitted.Connection.Id.ToString(),
                Outcome = ImportObjectStoreAudit.Succeeded,
                DisposeStream = true,
            };
        }

        var tempPath = PrivateTempFile.NewPath("honua-import-object-", Path.GetExtension(fileName));
        try
        {
            await using var file = PrivateTempFile.Create(tempPath, FileOptions.Asynchronous, bufferSize: 4096);
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        return new Admission
        {
            Request = ClearObjectStore(BaseRequest(command, fileName, admitted, head)) with { LocalFilePath = tempPath },
            SizeBytes = head.SizeBytes,
            ETag = head.ETag,
            AuditSourceUri = admitted.SourceUri,
            AuditConnectionId = admitted.Connection.Id.ToString(),
            Outcome = ImportObjectStoreAudit.Succeeded,
            DeleteLocalFile = true,
        };
    }

    private static Admission Denied(
        ImportObjectLocation location,
        ImportObjectStoreProblem problem,
        string outcome,
        ImportObjectHead? head = null)
        => new()
        {
            Problem = problem,
            SizeBytes = head?.SizeBytes,
            ETag = head?.ETag,
            AuditSourceUri = location.SourceUri,
            AuditConnectionId = location.Connection.Id.ToString(),
            Outcome = outcome,
        };

    private static ImportRequest BaseRequest(
        Command command,
        string fileName,
        ImportObjectLocation location,
        ImportObjectHead head)
        => new()
        {
            OperationInstanceId = command.OperationInstanceId,
            CorrelationId = command.CorrelationId,
            AuditId = command.AuditId,
            ProposalId = command.ProposalId,
            FileName = fileName,
            SourceKind = "object-store",
            SourceUrl = location.SourceUri,
            TableName = command.TableName,
            TargetSchema = command.TargetSchema,
            SourceSrid = command.SourceSrid,
            DatumTransformationWkid = command.DatumTransformationWkid,
            DatumTransformationName = command.DatumTransformationName,
            CoordinateEpoch = command.CoordinateEpoch,
            TargetSrid = command.TargetSrid,
            OverwriteExisting = command.OverwriteExisting,
            ObjectStoreUri = location.SourceUri,
            ObjectStoreETag = head.ETag,
            ObjectStoreConnectionId = location.Connection.Id.ToString(),
            ObjectStoreContentType = head.ContentType,
        };

    private static ImportRequest ClearObjectStore(ImportRequest request)
        => request with
        {
            ObjectStoreUri = null,
            ObjectStoreETag = null,
            ObjectStoreConnectionId = null,
            ObjectStoreContentType = null,
        };

    private static ImportObjectStoreProblem Unavailable(string detail)
        => new()
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            Title = "Service Unavailable",
            Detail = detail,
        };

    private static ImportObjectStoreProblem ProblemFor(ImportObjectStoreException exception)
    {
        var status = exception.Code switch
        {
            ImportObjectStoreProblems.NotFound => StatusCodes.Status404NotFound,
            ImportObjectStoreProblems.Changed => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden,
        };
        var title = exception.Code switch
        {
            ImportObjectStoreProblems.NotFound => "Object store source was not found",
            ImportObjectStoreProblems.Changed => "Object store source changed",
            _ => "Object store source forbidden",
        };
        return new ImportObjectStoreProblem
        {
            StatusCode = status,
            Title = title,
            Detail = exception.Message,
            Code = exception.Code,
        };
    }

    private static string OutcomeFor(ImportObjectStoreException exception)
        => exception.Code switch
        {
            ImportObjectStoreProblems.NotFound => ImportObjectStoreAudit.NotFound,
            ImportObjectStoreProblems.Changed => ImportObjectStoreAudit.Changed,
            ImportObjectStoreProblems.Forbidden or ImportObjectStoreProblems.RequesterPays => ImportObjectStoreAudit.Forbidden,
            _ => ImportObjectStoreAudit.Failed,
        };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort. The caller is already propagating the failure that required this delete.
            _ = ex;
        }
    }
}
