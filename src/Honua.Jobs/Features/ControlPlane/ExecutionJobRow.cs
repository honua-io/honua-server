// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

/// <summary>
/// How an <see cref="ExecutionJobRecord"/> is split across an <c>execution_jobs</c> row: ten
/// attempt-state fields are columns, everything else is the JSON record, and a few values of the
/// record are projected into columns for filtering and ordering.
/// </summary>
internal sealed record ExecutionJobRow(
    string RecordJson,
    byte[] RecordHash,
    string Kind,
    string RuntimeProfile,
    DateTimeOffset CreatedAt,
    string? TenantId,
    string[] LookupKeys)
{
    public const string TableName = "execution_jobs";

    /// <summary>Select list that <see cref="Read"/> expects, in order.</summary>
    public const string SelectColumns =
        "version, status, attempt_count, claimed_by, claimed_at, last_heartbeat_at, next_retry_at, "
        + "percent_complete, current_phase, updated_at, record";

    public const string RecordAssignments =
        "record = @record, record_hash = @record_hash, kind = @kind, runtime_profile = @runtime_profile, "
        + "created_at = @created_at, tenant_id = @tenant_id, lookup_keys = @lookup_keys";

    public static readonly string TerminalStatusList =
        $"'{nameof(ExecutionJobStatus.Succeeded)}', '{nameof(ExecutionJobStatus.Failed)}', '{nameof(ExecutionJobStatus.Cancelled)}'";

    public static readonly string ActiveStatusList =
        $"'{nameof(ExecutionJobStatus.Queued)}', '{nameof(ExecutionJobStatus.Provisioning)}', '{nameof(ExecutionJobStatus.Running)}'";

    public static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    public static ExecutionJobRow From(ExecutionJobRecord job)
    {
        var recordJson = JsonSerializer.Serialize(
            WithAttemptStatePlaceholders(job),
            ControlPlaneJsonContext.Default.ExecutionJobRecord);

        return new ExecutionJobRow(
            recordJson,
            SHA256.HashData(Encoding.UTF8.GetBytes(recordJson)),
            job.Spec.Kind.ToString(),
            RuntimeProfiles.Normalize(job.Spec.RuntimeProfile),
            ExecutionJobCursor.TruncateToMicroseconds(job.CreatedAt),
            job.Audit.SubmitterSecurityContext?.TenantId,
            BuildLookupKeys(job));
    }

    public static ExecutionJobRecord Read(NpgsqlDataReader reader)
    {
        var record = JsonSerializer.Deserialize(reader.GetString(10), ControlPlaneJsonContext.Default.ExecutionJobRecord)
            ?? throw new InvalidOperationException("An execution job row holds an empty record.");

        return record with
        {
            Version = reader.GetInt64(0),
            Status = Enum.Parse<ExecutionJobStatus>(reader.GetString(1)),
            AttemptCount = reader.GetInt32(2),
            ClaimedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
            ClaimedAt = ReadTimestamp(reader, 4),
            LastHeartbeatAt = ReadTimestamp(reader, 5),
            NextRetryAt = ReadTimestamp(reader, 6),
            PercentComplete = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            CurrentPhase = reader.IsDBNull(8) ? null : reader.GetString(8),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9)
        };
    }

    public void AddRecordParameters(NpgsqlCommand command)
    {
        command.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json) { Value = RecordJson });
        command.Parameters.Add(new NpgsqlParameter("record_hash", NpgsqlDbType.Bytea) { Value = RecordHash });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = Kind });
        command.Parameters.Add(new NpgsqlParameter("runtime_profile", NpgsqlDbType.Text) { Value = RuntimeProfile });
        command.Parameters.Add(Timestamp("created_at", CreatedAt));
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Text) { Value = (object?)TenantId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("lookup_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = LookupKeys });
    }

    public static NpgsqlParameter Timestamp(string name, DateTimeOffset? value)
        => new(name, NpgsqlDbType.TimestampTz) { Value = value.HasValue ? value.Value.UtcDateTime : DBNull.Value };

    public static string LookupKey(string dimension, string value)
        => string.Concat(dimension, ":", value.Trim().ToUpperInvariant());

    private static ExecutionJobRecord WithAttemptStatePlaceholders(ExecutionJobRecord job)
        => job with
        {
            Version = 0,
            Status = ExecutionJobStatus.Queued,
            AttemptCount = 0,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            NextRetryAt = null,
            PercentComplete = null,
            CurrentPhase = null,
            UpdatedAt = job.CreatedAt
        };

    private static string[] BuildLookupKeys(ExecutionJobRecord job)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Add(ExecutionJobLookupDimensions.Backend, job.Spec.Backend);
        Add(ExecutionJobLookupDimensions.Queue, ExecutionJobMetadata.ResolveQueue(job));
        Add(ExecutionJobLookupDimensions.RequestedBy, job.Audit.RequestedBy);
        Add(ExecutionJobLookupDimensions.Correlation, job.Audit.CorrelationId);
        Add(ExecutionJobLookupDimensions.Trace, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.TraceId));
        Add(ExecutionJobLookupDimensions.Definition, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.DefinitionId));
        Add(ExecutionJobLookupDimensions.Definition, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.GeoprocessingPlanId));
        Add(ExecutionJobLookupDimensions.Environment, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.Environment));
        Add(ExecutionJobLookupDimensions.Server, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.Server));
        Add(ExecutionJobLookupDimensions.Release, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.ReleaseId));
        Add(ExecutionJobLookupDimensions.ChangeSet, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.ChangeSetId));
        Add(ExecutionJobLookupDimensions.Alert, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.AlertId));

        var resourceRefs = ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.ResourceRefs);
        if (resourceRefs is not null)
        {
            foreach (var resourceRef in resourceRefs.Split(
                         ExecutionJobParameterKeys.MetadataListSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(ExecutionJobLookupDimensions.Resource, resourceRef);
            }
        }

        return [.. keys];

        void Add(string dimension, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                keys.Add(LookupKey(dimension, value));
            }
        }
    }

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}

internal static class ExecutionJobLookupDimensions
{
    public const string Backend = "backend";
    public const string Queue = "queue";
    public const string RequestedBy = "requested-by";
    public const string Correlation = "correlation";
    public const string Trace = "trace";
    public const string Definition = "definition";
    public const string Resource = "resource";
    public const string Environment = "environment";
    public const string Server = "server";
    public const string Release = "release";
    public const string ChangeSet = "change-set";
    public const string Alert = "alert";
}
