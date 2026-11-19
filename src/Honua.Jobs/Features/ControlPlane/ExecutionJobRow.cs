// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Honua.ControlPlane;

// How an ExecutionJobRecord is laid out in an execution_jobs row: the whole
// record as JSON, the attempt-state fields as database-stamped columns that override their JSON
// copies when read, and a few values of the record projected into columns for filtering.
internal sealed record ExecutionJobRow(
    string RecordJson,
    string Kind,
    string RuntimeProfile,
    string Backend,
    short Priority,
    DateTimeOffset CreatedAt,
    string? TenantId,
    bool CancellationRequested,
    TimeSpan HeartbeatTimeout,
    TimeSpan MaxDuration,
    string[] LookupKeys)
{
    public const string TableName = "execution_jobs";

    // Select list that Read expects, in order. operation_id and created_at come last so a row
    // whose record cannot be read can still be named and paged past.
    public const string SelectColumns =
        "version, status, attempt_count, claimed_by, claimed_at, last_heartbeat_at, next_retry_at, "
        + "completed_at, percent_complete, current_phase, updated_at, record, operation_id, created_at";

    // The phase of a job failed because its stored record cannot be read.
    public const string UnreadableRecordPhase = "Failed: unreadable record";

    // Assignments that fail a job whose stored record cannot be read. The record is left as it was
    // for diagnosis; the columns alone make the job terminal.
    public const string FailUnreadableAssignments =
        "status = 'Failed', version = version + 1, claimed_by = NULL, claimed_at = NULL, "
        + "last_heartbeat_at = NULL, next_retry_at = NULL, queue_visible_at = NULL, queue_priority = NULL, "
        + "current_phase = '" + UnreadableRecordPhase + "', completed_at = now(), updated_at = now()";

    public const string RecordColumns =
        "record, kind, runtime_profile, backend, priority, created_at, tenant_id, cancellation_requested, "
        + "heartbeat_timeout, max_duration, lookup_keys";

    public const string RecordParameters =
        "@record, @kind, @runtime_profile, @backend, @priority, @created_at, @tenant_id, @cancellation_requested, "
        + "@heartbeat_timeout, @max_duration, @lookup_keys";

    public const string RecordAssignments =
        "record = @record, kind = @kind, runtime_profile = @runtime_profile, backend = @backend, "
        + "priority = @priority, created_at = @created_at, tenant_id = @tenant_id, "
        + "cancellation_requested = @cancellation_requested, heartbeat_timeout = @heartbeat_timeout, "
        + "max_duration = @max_duration, lookup_keys = @lookup_keys";

    public const string LocalBackend = LocalBatchComputeBackend.BackendId;

    public static readonly string TerminalStatusList =
        $"'{nameof(ExecutionJobStatus.Succeeded)}', '{nameof(ExecutionJobStatus.Failed)}', '{nameof(ExecutionJobStatus.Cancelled)}'";

    public static readonly string ActiveStatusList =
        $"'{nameof(ExecutionJobStatus.Queued)}', '{nameof(ExecutionJobStatus.Provisioning)}', '{nameof(ExecutionJobStatus.Running)}'";

    public static ExecutionJobRow From(ExecutionJobRecord job, IExecutionJobSecretProtector secrets)
    {
        job = ExecutionJobSubmitterSeal.Seal(job, secrets);
        return new(
            JsonSerializer.Serialize(ExecutionJobSecretParameters.Protect(job, secrets), ControlPlaneJsonContext.Default.ExecutionJobRecord),
            job.Spec.Kind.ToString(),
            RuntimeProfiles.Normalize(job.Spec.RuntimeProfile),
            job.Spec.Backend,
            PriorityBand(job.Priority),
            ExecutionJobCursor.TruncateToMicroseconds(job.CreatedAt),
            job.Audit.SubmitterSecurityContext?.TenantId,
            job.CancellationRequestedAt.HasValue,
            (job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default).Timeout,
            (job.TimeoutPolicy ?? JobTimeoutPolicy.Default).MaxDuration,
            BuildLookupKeys(job));
    }

    public static short PriorityBand(OperationPriority priority)
        => priority switch
        {
            OperationPriority.Critical => 0,
            OperationPriority.High => 1,
            OperationPriority.Low => 3,
            _ => 2
        };

    public static ExecutionJobRecord Read(NpgsqlDataReader reader, IExecutionJobSecretProtector secrets)
        => TryRead(reader, secrets) ?? throw new InvalidOperationException(
            $"Execution job {ReadOperationId(reader)} holds a record this build cannot read.");

    public static string ReadOperationId(NpgsqlDataReader reader) => reader.GetString(12);

    public static ExecutionJobCursor ReadCursor(NpgsqlDataReader reader)
        => new(reader.GetFieldValue<DateTimeOffset>(13), ReadOperationId(reader));

    // Null when the stored record is empty or not a record this build can deserialize, for
    // example after corruption or when a newer build wrote it. Callers that scan many rows skip
    // such a row instead of failing the whole read.
    public static ExecutionJobRecord? TryRead(NpgsqlDataReader reader, IExecutionJobSecretProtector secrets)
    {
        ExecutionJobRecord? record;
        try
        {
            record = JsonSerializer.Deserialize(reader.GetString(11), ControlPlaneJsonContext.Default.ExecutionJobRecord);
        }
        catch (JsonException)
        {
            return null;
        }

        if (record is null)
        {
            return null;
        }

        record = ExecutionJobSubmitterSeal.Verify(record, secrets);
        return ExecutionJobSecretParameters.Unprotect(record, secrets) with
        {
            Version = reader.GetInt64(0),
            Status = Enum.Parse<ExecutionJobStatus>(reader.GetString(1)),
            AttemptCount = reader.GetInt32(2),
            ClaimedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
            ClaimedAt = ReadTimestamp(reader, 4),
            LastHeartbeatAt = ReadTimestamp(reader, 5),
            NextRetryAt = ReadTimestamp(reader, 6),
            CompletedAt = ReadTimestamp(reader, 7),
            PercentComplete = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            CurrentPhase = reader.IsDBNull(9) ? null : reader.GetString(9),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10)
        };
    }

    public void AddRecordParameters(NpgsqlCommand command)
    {
        command.Parameters.Add(new NpgsqlParameter("record", NpgsqlDbType.Json) { Value = RecordJson });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = Kind });
        command.Parameters.Add(new NpgsqlParameter("runtime_profile", NpgsqlDbType.Text) { Value = RuntimeProfile });
        command.Parameters.Add(new NpgsqlParameter("backend", NpgsqlDbType.Text) { Value = Backend });
        command.Parameters.Add(new NpgsqlParameter("priority", NpgsqlDbType.Smallint) { Value = Priority });
        command.Parameters.Add(new NpgsqlParameter("cancellation_requested", NpgsqlDbType.Boolean) { Value = CancellationRequested });
        command.Parameters.Add(new NpgsqlParameter("heartbeat_timeout", NpgsqlDbType.Interval) { Value = HeartbeatTimeout });
        command.Parameters.Add(new NpgsqlParameter("max_duration", NpgsqlDbType.Interval) { Value = MaxDuration });
        command.Parameters.Add(Timestamp("created_at", CreatedAt));
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Text) { Value = (object?)TenantId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("lookup_keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = LookupKeys });
    }

    public static NpgsqlParameter Timestamp(string name, DateTimeOffset? value)
        => new(name, NpgsqlDbType.TimestampTz) { Value = value.HasValue ? value.Value.UtcDateTime : DBNull.Value };

    public static string LookupKey(string dimension, string value)
        => string.Concat(dimension, ":", value.Trim().ToUpperInvariant());

    private static string[] BuildLookupKeys(ExecutionJobRecord job)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Add(ExecutionJobLookupDimensions.Backend, job.Spec.Backend);
        Add(ExecutionJobLookupDimensions.Queue, ExecutionJobMetadata.ResolveQueue(job));
        Add(ExecutionJobLookupDimensions.RequestedBy, job.Audit.RequestedBy);
        Add(ExecutionJobLookupDimensions.AdmissionRate, job.AdmissionRatePrincipal);
        Add(ExecutionJobLookupDimensions.Correlation, job.Audit.CorrelationId);
        Add(ExecutionJobLookupDimensions.Trace, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.TraceId));
        Add(ExecutionJobLookupDimensions.Definition, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.DefinitionId));
        Add(ExecutionJobLookupDimensions.Definition, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.GeoprocessingPlanId));
        Add(ExecutionJobLookupDimensions.Environment, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.Environment));
        Add(ExecutionJobLookupDimensions.Server, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.Server));
        Add(ExecutionJobLookupDimensions.Release, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.ReleaseId));
        Add(ExecutionJobLookupDimensions.ChangeSet, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.ChangeSetId));
        Add(ExecutionJobLookupDimensions.Alert, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.AlertId));
        Add(ExecutionJobLookupDimensions.OrchestrationRun, ExecutionJobMetadata.GetParameter(job, ExecutionJobParameterKeys.OrchestrationRunId));

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
    public const string OrchestrationRun = "run";
    public const string AdmissionRate = "admission-rate";
}
