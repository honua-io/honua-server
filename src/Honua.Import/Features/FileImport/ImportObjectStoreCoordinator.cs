// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;

namespace Honua.Import.FileImport;

/// <summary>
/// Decides whether an object-store import is refused, streamed in this request, or queued.
/// HeadObject runs before any body read. A background or over-limit object is never opened here.
/// </summary>
internal static class ImportObjectStoreCoordinator
{
    internal const string ReaderUnavailableDetail = "Object store import is not available on this host.";

    internal const string UnsupportedExtensionDetail = "The object does not have a supported import extension.";

    internal sealed record Command
    {
        public required string SourceUri { get; init; }

        public string? AssumeRoleArn { get; init; }

        public required string TableName { get; init; }

        public string? TargetSchema { get; init; }

        public int? SourceSrid { get; init; }

        public int TargetSrid { get; init; } = 4326;

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

        public string? AuditRoleArn { get; init; }

        public required string Outcome { get; init; }

        public bool DisposeStream { get; init; }

        public bool DeleteLocalFile { get; init; }
    }

    public static async Task<Admission> EvaluateAsync(
        Command command,
        ImportObjectStoreOptions options,
        ImportLimits limits,
        IImportObjectStoreReader? reader,
        bool canAcceptBackground,
        Func<string, bool> formatSupported,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(formatSupported);

        if (!ImportObjectStorePolicy.TryAdmit(
                command.SourceUri,
                command.AssumeRoleArn,
                options,
                out var location,
                out var problem))
        {
            var embedsCredentials = problem?.Detail == "sourceUri must not embed credentials.";
            return new Admission
            {
                Problem = problem,
                AuditSourceUri = embedsCredentials ? null : command.SourceUri.Trim(),
                AuditRoleArn = embedsCredentials ? null : TrimOrNull(command.AssumeRoleArn),
                Outcome = OutcomeFor(problem),
            };
        }

        var admitted = location!;
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
        if (!formatSupported(fileName))
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
            if (!canAcceptBackground)
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
                AuditRoleArn = admitted.AssumeRoleArn,
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
                AuditRoleArn = admitted.AssumeRoleArn,
                Outcome = ImportObjectStoreAudit.Succeeded,
                DisposeStream = true,
            };
        }

        var tempPath = Path.Join(Path.GetTempPath(), $"honua-import-object-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        try
        {
            await using (var file = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            }))
            {
                await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
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
            AuditRoleArn = admitted.AssumeRoleArn,
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
            AuditRoleArn = location.AssumeRoleArn,
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
            TargetSrid = command.TargetSrid,
            OverwriteExisting = command.OverwriteExisting,
            ObjectStoreUri = location.SourceUri,
            ObjectStoreETag = head.ETag,
            ObjectStoreRoleArn = location.AssumeRoleArn,
            ObjectStoreContentType = head.ContentType,
        };

    private static ImportRequest ClearObjectStore(ImportRequest request)
        => request with
        {
            ObjectStoreUri = null,
            ObjectStoreETag = null,
            ObjectStoreRoleArn = null,
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

    private static string OutcomeFor(ImportObjectStoreProblem? problem)
        => problem?.Code switch
        {
            ImportObjectStoreProblems.Forbidden => ImportObjectStoreAudit.Forbidden,
            ImportObjectStoreProblems.NotFound => ImportObjectStoreAudit.NotFound,
            ImportObjectStoreProblems.TooLarge => ImportObjectStoreAudit.TooLarge,
            ImportObjectStoreProblems.Changed => ImportObjectStoreAudit.Changed,
            ImportObjectStoreProblems.UnsupportedScheme => ImportObjectStoreAudit.UnsupportedScheme,
            _ => problem?.StatusCode == StatusCodes.Status503ServiceUnavailable
                ? ImportObjectStoreAudit.Unavailable
                : ImportObjectStoreAudit.Failed,
        };

    private static string OutcomeFor(ImportObjectStoreException exception)
        => exception.Code switch
        {
            ImportObjectStoreProblems.NotFound => ImportObjectStoreAudit.NotFound,
            ImportObjectStoreProblems.Changed => ImportObjectStoreAudit.Changed,
            ImportObjectStoreProblems.Forbidden => ImportObjectStoreAudit.Forbidden,
            _ => ImportObjectStoreAudit.Failed,
        };

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
