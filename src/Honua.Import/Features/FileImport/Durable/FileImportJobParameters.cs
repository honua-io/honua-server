// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Domain;

namespace Honua.Import.FileImport.Durable;

/// <summary>
/// The <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobSpec.Parameters"/> contract
/// of a <c>FileImport</c> execution job (honua-server#5916). The accepting host writes it; the
/// worker reads it back into the <see cref="ImportRequest"/> the in-process path would have run.
/// The source is always the staged object-store file (<c>import.cloud_file_id</c>): a worker on
/// another machine cannot read the accepting host's local disk.
/// </summary>
internal static class FileImportJobParameters
{
    public const string CloudFileId = "import.cloud_file_id";
    public const string FileName = "import.file_name";
    public const string FileSizeBytes = "import.file_size_bytes";
    public const string TableName = "import.table_name";
    public const string TargetSchema = "import.target_schema";
    public const string SourceSrid = "import.source_srid";
    public const string TargetSrid = "import.target_srid";
    public const string OverwriteExisting = "import.overwrite_existing";
    public const string LoadMode = "import.load_mode";
    public const string KeyColumns = "import.key_columns";
    public const string SourceKind = "import.source_kind";
    public const string SourceUrl = "import.source_url";
    public const string UploadId = "import.upload_id";
    public const string OperationInstanceId = "import.operation_instance_id";
    public const string CorrelationId = "import.correlation_id";
    public const string AuditId = "import.audit_id";
    public const string ProposalId = "import.proposal_id";

    /// <summary>The runner the accepting host routed the job to (<c>batch</c> or <c>in-process</c>).</summary>
    public const string Runner = "import.runner";

    /// <summary>Projects a staged import request onto job parameters.</summary>
    /// <exception cref="InvalidOperationException">The request is not staged in object storage.</exception>
    public static Dictionary<string, string> Build(ImportRequest request, long fileSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UsesCloudStorage)
        {
            throw new InvalidOperationException(
                "A durable file-import job requires the source to be staged in shared file storage (CloudFileId).");
        }

        if (request.CsvOptions is not null)
        {
            throw new NotSupportedException("CSV import options are not carried by durable file-import jobs.");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CloudFileId] = request.CloudFileId!,
            [FileName] = request.FileName,
            [FileSizeBytes] = fileSizeBytes.ToString(CultureInfo.InvariantCulture),
            [TableName] = request.TableName,
            [TargetSrid] = request.TargetSrid.ToString(CultureInfo.InvariantCulture),
            [OverwriteExisting] = request.OverwriteExisting ? "true" : "false",
            [LoadMode] = request.LoadMode.ToString(),
            [SourceKind] = request.SourceKind,
        };

        AddIfPresent(parameters, TargetSchema, request.TargetSchema);
        AddIfPresent(parameters, SourceSrid, request.SourceSrid?.ToString(CultureInfo.InvariantCulture));
        AddIfPresent(parameters, KeyColumns, request.KeyColumns.Count == 0 ? null : string.Join(',', request.KeyColumns));
        AddIfPresent(parameters, SourceUrl, request.SourceUrl);
        AddIfPresent(parameters, UploadId, request.UploadId);
        AddIfPresent(parameters, OperationInstanceId, request.OperationInstanceId);
        AddIfPresent(parameters, CorrelationId, request.CorrelationId);
        AddIfPresent(parameters, AuditId, request.AuditId);
        AddIfPresent(parameters, ProposalId, request.ProposalId);
        return parameters;
    }

    /// <summary>Reads the import request back from job parameters.</summary>
    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out ImportRequest? request,
        out long fileSizeBytes,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        request = null;
        fileSizeBytes = 0;
        error = null;

        var cloudFileId = Get(parameters, CloudFileId);
        var fileName = Get(parameters, FileName);
        var tableName = Get(parameters, TableName);
        if (cloudFileId is null || fileName is null || tableName is null)
        {
            error = $"'{CloudFileId}', '{FileName}' and '{TableName}' are required.";
            return false;
        }

        if (!int.TryParse(Get(parameters, TargetSrid) ?? "4326", NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetSrid))
        {
            error = $"'{TargetSrid}' is not an integer.";
            return false;
        }

        int? sourceSrid = null;
        if (Get(parameters, SourceSrid) is { } sourceSridText)
        {
            if (!int.TryParse(sourceSridText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSource))
            {
                error = $"'{SourceSrid}' is not an integer.";
                return false;
            }

            sourceSrid = parsedSource;
        }

        var loadMode = ImportLoadMode.Replace;
        if (Get(parameters, LoadMode) is { } loadModeText
            && !Enum.TryParse(loadModeText, ignoreCase: false, out loadMode))
        {
            error = $"'{LoadMode}' value '{loadModeText}' is not a known load mode.";
            return false;
        }

        _ = long.TryParse(Get(parameters, FileSizeBytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out fileSizeBytes);

        request = new ImportRequest
        {
            CloudFileId = cloudFileId,
            FileName = fileName,
            TableName = tableName,
            TargetSchema = Get(parameters, TargetSchema),
            SourceSrid = sourceSrid,
            TargetSrid = targetSrid,
            OverwriteExisting = string.Equals(Get(parameters, OverwriteExisting), "true", StringComparison.Ordinal),
            LoadMode = loadMode,
            KeyColumns = Get(parameters, KeyColumns) is { } keys
                ? keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [],
            SourceKind = Get(parameters, SourceKind) ?? "cloud-file",
            SourceUrl = Get(parameters, SourceUrl),
            UploadId = Get(parameters, UploadId),
            OperationInstanceId = Get(parameters, OperationInstanceId),
            CorrelationId = Get(parameters, CorrelationId),
            AuditId = Get(parameters, AuditId),
            ProposalId = Get(parameters, ProposalId),
        };
        return true;
    }

    private static string? Get(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static void AddIfPresent(Dictionary<string, string> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters[key] = value;
        }
    }
}
