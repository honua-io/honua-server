// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Forms;

internal sealed class PostgresFormPackageStore : IFormPackageStore
{
    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly string _packagesTable;
    private readonly string _versionsTable;
    private readonly string _submissionsTable;
    private readonly string _attachmentsTable;

    public PostgresFormPackageStore(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _packagesTable = SchemaSearchPath.QualifyTable("form_packages", "honua");
        _versionsTable = SchemaSearchPath.QualifyTable("form_package_versions", "honua");
        _submissionsTable = SchemaSearchPath.QualifyTable("form_submissions", "honua");
        _attachmentsTable = SchemaSearchPath.QualifyTable("form_submission_attachments", "honua");
    }

    public async Task<FormPackageSummary[]> ListPackagesAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT form_id, title, service_id, layer_id, current_draft_version, current_published_version, updated_at
            FROM {_packagesTable}
            ORDER BY updated_at DESC, form_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var packages = new List<FormPackageSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            packages.Add(new FormPackageSummary
            {
                FormId = reader.GetString(0),
                Title = reader.GetString(1),
                ServiceId = reader.GetString(2),
                LayerId = reader.GetInt32(3),
                CurrentDraftVersion = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                CurrentPublishedVersion = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6)
            });
        }

        return packages.ToArray();
    }

    public async Task<FormPackageVersion?> GetCurrentVersionAsync(
        string formId,
        string status,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var currentColumn = string.Equals(status, FormPackageStatus.Published, StringComparison.OrdinalIgnoreCase)
            ? "current_published_version"
            : "current_draft_version";
        var sql = $"""
            SELECT v.form_id, v.version, v.status, v.package_json::text, v.validation_json::text,
                   v.content_hash, v.policy_hash, v.etag, v.created_at, v.created_by,
                   v.published_at, v.published_by, v.reopened_from_version
            FROM {_packagesTable} p
            JOIN {_versionsTable} v ON v.form_id = p.form_id AND v.version = p.{currentColumn}
            WHERE p.form_id = @form_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadVersion(reader)
            : null;
    }

    public async Task<FormPackageVersion?> GetVersionAsync(
        string formId,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);

        var sql = SelectVersionSql() + " WHERE form_id = @form_id AND version = @version";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadVersion(reader)
            : null;
    }

    public async Task<FormPackageVersion[]> ListVersionsAsync(
        string formId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        var sql = SelectVersionSql() + " WHERE form_id = @form_id ORDER BY version DESC";

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var versions = new List<FormPackageVersion>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(ReadVersion(reader));
        }

        return versions.ToArray();
    }

    public async Task<FormPackageVersion> SaveDraftAsync(
        FormPackageDocument package,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var formId = string.IsNullOrWhiteSpace(package.FormId)
            ? $"form-{Guid.NewGuid():N}"
            : package.FormId.Trim();
        var normalized = ClonePackage(package, formId);
        var packageJson = SerializePackage(normalized);
        var contentHash = Hash(packageJson);
        var policyHash = HashPolicy(normalized);
        var etag = QuoteETag(contentHash);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        var version = await GetNextVersionAsync(connection, transaction, formId, cancellationToken).ConfigureAwait(false);
        await UpsertPackageFamilyAsync(connection, transaction, normalized, currentDraftVersion: version, cancellationToken).ConfigureAwait(false);
        await InsertVersionAsync(
            connection,
            transaction,
            normalized,
            version,
            FormPackageStatus.Draft,
            validationJson: null,
            contentHash,
            policyHash,
            etag,
            createdBy: actor,
            publishedBy: null,
            reopenedFromVersion: null,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetVersionAsync(formId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Saved form package version could not be read back.");
    }

    public async Task<FormPackageVersion?> UpdateDraftAsync(
        string formId,
        int version,
        FormPackageDocument package,
        string expectedETag,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var normalized = ClonePackage(package, formId);
        var packageJson = SerializePackage(normalized);
        var contentHash = Hash(packageJson);
        var policyHash = HashPolicy(normalized);
        var etag = QuoteETag(contentHash);
        var sql = $"""
            UPDATE {_versionsTable}
            SET package_json = @package_json,
                validation_json = NULL,
                content_hash = @content_hash,
                policy_hash = @policy_hash,
                etag = @etag,
                created_by = @actor
            WHERE form_id = @form_id
              AND version = @version
              AND status = @status
              AND etag = @expected_etag
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            AddVersionMutationParameters(command, normalized, contentHash, policyHash, etag);
            command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, actor);
            command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
            command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
            command.Parameters.AddWithValue("status", NpgsqlDbType.Text, FormPackageStatus.Draft);
            command.Parameters.AddWithValue("expected_etag", NpgsqlDbType.Text, expectedETag);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        await UpsertPackageFamilyAsync(connection, transaction, normalized, currentDraftVersion: version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetVersionAsync(formId, version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FormPackageVersion?> StoreValidationAsync(
        string formId,
        int version,
        FormPackageValidationResult validation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(validation);

        var validationJson = SerializeValidation(validation);
        var sql = $"""
            UPDATE {_versionsTable}
            SET validation_json = @validation_json
            WHERE form_id = @form_id AND version = @version AND status = @status
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("validation_json", NpgsqlDbType.Jsonb, validationJson);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, FormPackageStatus.Draft);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0
            ? null
            : await GetVersionAsync(formId, version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FormPackageVersion?> PublishAsync(
        string formId,
        int version,
        FormPackageValidationResult validation,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var sql = $"""
            UPDATE {_versionsTable}
            SET status = @published_status,
                validation_json = @validation_json,
                published_at = now(),
                published_by = @actor
            WHERE form_id = @form_id
              AND version = @version
              AND status = @draft_status
            """;
        var packageSql = $"""
            UPDATE {_packagesTable}
            SET current_published_version = @version,
                current_draft_version = CASE
                    WHEN current_draft_version = @version THEN NULL
                    ELSE current_draft_version
                END,
                updated_at = now()
            WHERE form_id = @form_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("published_status", NpgsqlDbType.Text, FormPackageStatus.Published);
            command.Parameters.AddWithValue("validation_json", NpgsqlDbType.Jsonb, SerializeValidation(validation));
            command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, actor);
            command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
            command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
            command.Parameters.AddWithValue("draft_status", NpgsqlDbType.Text, FormPackageStatus.Draft);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        await using (var command = new NpgsqlCommand(packageSql, connection, transaction))
        {
            command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
            command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetVersionAsync(formId, version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FormPackageVersion?> ReopenAsync(
        string formId,
        int publishedVersion,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(publishedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var source = await GetVersionAsync(formId, publishedVersion, cancellationToken).ConfigureAwait(false);
        if (source is null || !string.Equals(source.Status, FormPackageStatus.Published, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = ClonePackage(source.Package, formId);
        var packageJson = SerializePackage(normalized);
        var contentHash = Hash(packageJson);
        var policyHash = HashPolicy(normalized);
        var etag = QuoteETag(Hash($"{contentHash}:{Guid.NewGuid():N}"));

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        var version = await GetNextVersionAsync(connection, transaction, formId, cancellationToken).ConfigureAwait(false);
        await UpsertPackageFamilyAsync(connection, transaction, normalized, currentDraftVersion: version, cancellationToken).ConfigureAwait(false);
        await InsertVersionAsync(
            connection,
            transaction,
            normalized,
            version,
            FormPackageStatus.Draft,
            validationJson: null,
            contentHash,
            policyHash,
            etag,
            actor,
            publishedBy: null,
            reopenedFromVersion: publishedVersion,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetVersionAsync(formId, version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FormSubmissionRecord?> GetSubmissionByIdempotencyAsync(
        string formId,
        int formVersion,
        string actorHash,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(formVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var sql = $"""
            SELECT submission_id, request_hash, response_payload::text, status
            FROM {_submissionsTable}
            WHERE form_id = @form_id
              AND form_version = @form_version
              AND actor_hash = @actor_hash
              AND idempotency_key = @idempotency_key
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
        command.Parameters.AddWithValue("form_version", NpgsqlDbType.Integer, formVersion);
        command.Parameters.AddWithValue("actor_hash", NpgsqlDbType.Text, actorHash);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var responseJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new FormSubmissionRecord
        {
            SubmissionId = reader.GetGuid(0),
            RequestHash = reader.GetString(1),
            Response = string.IsNullOrWhiteSpace(responseJson)
                ? null
                : JsonSerializer.Deserialize(responseJson, FormPackageJsonContext.Default.FormSubmissionResponse),
            Status = reader.GetString(3)
        };
    }

    public async Task CreateSubmissionAsync(
        Guid submissionId,
        string? idempotencyKey,
        string actorHash,
        string requestHash,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        string status,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentNullException.ThrowIfNull(packageVersion);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var sql = $"""
            INSERT INTO {_submissionsTable} (
                submission_id, idempotency_key, actor_hash, form_id, form_version,
                service_id, layer_id, target_feature_id, operation, status,
                package_hash, policy_hash, request_hash, request_summary, created_at)
            VALUES (
                @submission_id, @idempotency_key, @actor_hash, @form_id, @form_version,
                @service_id, @layer_id, @target_feature_id, @operation, @status,
                @package_hash, @policy_hash, @request_hash, @request_summary, now())
            ON CONFLICT (form_id, form_version, actor_hash, idempotency_key)
                WHERE idempotency_key IS NOT NULL
            DO NOTHING
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        command.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text, (object?)idempotencyKey ?? DBNull.Value);
        command.Parameters.AddWithValue("actor_hash", NpgsqlDbType.Text, actorHash);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, packageVersion.FormId);
        command.Parameters.AddWithValue("form_version", NpgsqlDbType.Integer, packageVersion.Version);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, packageVersion.Package.Target?.ServiceId ?? string.Empty);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, packageVersion.Package.Target?.LayerId ?? 0);
        command.Parameters.AddWithValue("target_feature_id", NpgsqlDbType.Bigint, (object?)request.TargetFeatureId ?? DBNull.Value);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, request.Operation);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, status);
        command.Parameters.AddWithValue("package_hash", NpgsqlDbType.Text, packageVersion.ContentHash);
        command.Parameters.AddWithValue("policy_hash", NpgsqlDbType.Text, packageVersion.PolicyHash);
        command.Parameters.AddWithValue("request_hash", NpgsqlDbType.Text, requestHash);
        command.Parameters.AddWithValue("request_summary", NpgsqlDbType.Jsonb, BuildRequestSummaryJson(request, packageVersion.Package));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSubmissionAsync(
        Guid submissionId,
        FormSubmissionResponse response,
        string status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var sql = $"""
            UPDATE {_submissionsTable}
            SET status = @status,
                response_payload = @response_payload,
                validation_json = @validation_json,
                target_feature_id = COALESCE(@target_feature_id, target_feature_id),
                completed_at = now()
            WHERE submission_id = @submission_id
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, status);
        command.Parameters.AddWithValue("response_payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(response, FormPackageJsonContext.Default.FormSubmissionResponse));
        command.Parameters.AddWithValue("validation_json", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(response.ValidationIssues, FormPackageJsonContext.Default.FormValidationIssueArray));
        command.Parameters.AddWithValue("target_feature_id", NpgsqlDbType.Bigint, (object?)response.TargetFeatureId ?? DBNull.Value);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAttachmentOutcomeAsync(
        Guid submissionId,
        FormSubmissionAttachmentDescriptor descriptor,
        FormSubmissionAttachmentOutcome outcome,
        FormPackageVersion packageVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(packageVersion);

        var sql = $"""
            INSERT INTO {_attachmentsTable} (
                submission_id, field_id, client_attachment_id, filename, content_type,
                size_bytes, sha256, part_name, attachment_id, privacy_policy,
                status, failure_reason, created_at)
            VALUES (
                @submission_id, @field_id, @client_attachment_id, @filename, @content_type,
                @size_bytes, @sha256, @part_name, @attachment_id, @privacy_policy,
                @status, @failure_reason, now())
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Uuid, submissionId);
        command.Parameters.AddWithValue("field_id", NpgsqlDbType.Text, (object?)descriptor.FieldId ?? DBNull.Value);
        command.Parameters.AddWithValue("client_attachment_id", NpgsqlDbType.Text, (object?)descriptor.ClientAttachmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("filename", NpgsqlDbType.Text, (object?)descriptor.Filename ?? DBNull.Value);
        command.Parameters.AddWithValue("content_type", NpgsqlDbType.Text, (object?)descriptor.ContentType ?? DBNull.Value);
        command.Parameters.AddWithValue("size_bytes", NpgsqlDbType.Bigint, (object?)descriptor.SizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("sha256", NpgsqlDbType.Text, (object?)descriptor.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("part_name", NpgsqlDbType.Text, (object?)descriptor.PartName ?? DBNull.Value);
        command.Parameters.AddWithValue("attachment_id", NpgsqlDbType.Bigint, (object?)outcome.AttachmentId ?? DBNull.Value);
        command.Parameters.AddWithValue("privacy_policy", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(packageVersion.Package.PrivacyPolicy, FormPackageJsonContext.Default.FormPrivacyPolicy));
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, outcome.Status);
        command.Parameters.AddWithValue("failure_reason", NpgsqlDbType.Text, (object?)outcome.Reason ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string SelectVersionSql()
        => $"""
            SELECT form_id, version, status, package_json::text, validation_json::text,
                   content_hash, policy_hash, etag, created_at, created_by,
                   published_at, published_by, reopened_from_version
            FROM {_versionsTable}
            """;

    private static FormPackageVersion ReadVersion(NpgsqlDataReader reader)
    {
        var packageJson = reader.GetString(3);
        var validationJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new FormPackageVersion
        {
            FormId = reader.GetString(0),
            Version = reader.GetInt32(1),
            Status = reader.GetString(2),
            Package = JsonSerializer.Deserialize(packageJson, FormPackageJsonContext.Default.FormPackageDocument) ?? new FormPackageDocument(),
            Validation = string.IsNullOrWhiteSpace(validationJson)
                ? null
                : JsonSerializer.Deserialize(validationJson, FormPackageJsonContext.Default.FormPackageValidationResult),
            ContentHash = reader.GetString(5),
            PolicyHash = reader.GetString(6),
            ETag = reader.GetString(7),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
            CreatedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
            PublishedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            PublishedBy = reader.IsDBNull(11) ? null : reader.GetString(11),
            ReopenedFromVersion = reader.IsDBNull(12) ? null : reader.GetInt32(12)
        };
    }

    private async Task<int> GetNextVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string formId,
        CancellationToken cancellationToken)
    {
        const string lockSql = """
            SELECT pg_advisory_xact_lock(hashtext(@form_id))
            """;
        await using (var lockCommand = new NpgsqlCommand(lockSql, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var sql = $"""
            SELECT COALESCE(MAX(version), 0) + 1
            FROM {_versionsTable}
            WHERE form_id = @form_id
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, formId);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 1);
    }

    private async Task UpsertPackageFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FormPackageDocument package,
        int currentDraftVersion,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_packagesTable} (
                form_id, title, service_id, layer_id, current_draft_version, created_at, updated_at)
            VALUES (
                @form_id, @title, @service_id, @layer_id, @current_draft_version, now(), now())
            ON CONFLICT (form_id) DO UPDATE
            SET title = EXCLUDED.title,
                service_id = EXCLUDED.service_id,
                layer_id = EXCLUDED.layer_id,
                current_draft_version = @current_draft_version,
                updated_at = now()
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, package.FormId ?? string.Empty);
        command.Parameters.AddWithValue("title", NpgsqlDbType.Text, package.Title ?? package.FormId ?? string.Empty);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, package.Target?.ServiceId ?? string.Empty);
        command.Parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, package.Target?.LayerId ?? 0);
        command.Parameters.AddWithValue("current_draft_version", NpgsqlDbType.Integer, currentDraftVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FormPackageDocument package,
        int version,
        string status,
        string? validationJson,
        string contentHash,
        string policyHash,
        string etag,
        string createdBy,
        string? publishedBy,
        int? reopenedFromVersion,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO {_versionsTable} (
                form_id, version, status, package_json, validation_json,
                content_hash, policy_hash, etag, created_at, created_by,
                published_at, published_by, reopened_from_version)
            VALUES (
                @form_id, @version, @status, @package_json, @validation_json,
                @content_hash, @policy_hash, @etag, now(), @created_by,
                CASE WHEN @status = @published_status THEN now() ELSE NULL END,
                @published_by, @reopened_from_version)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("form_id", NpgsqlDbType.Text, package.FormId ?? string.Empty);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, version);
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, status);
        AddVersionMutationParameters(command, package, contentHash, policyHash, etag);
        command.Parameters.AddWithValue("validation_json", NpgsqlDbType.Jsonb, (object?)validationJson ?? DBNull.Value);
        command.Parameters.AddWithValue("created_by", NpgsqlDbType.Text, createdBy);
        command.Parameters.AddWithValue("published_status", NpgsqlDbType.Text, FormPackageStatus.Published);
        command.Parameters.AddWithValue("published_by", NpgsqlDbType.Text, (object?)publishedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("reopened_from_version", NpgsqlDbType.Integer, (object?)reopenedFromVersion ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddVersionMutationParameters(
        NpgsqlCommand command,
        FormPackageDocument package,
        string contentHash,
        string policyHash,
        string etag)
    {
        command.Parameters.AddWithValue("package_json", NpgsqlDbType.Jsonb, SerializePackage(package));
        command.Parameters.AddWithValue("content_hash", NpgsqlDbType.Text, contentHash);
        command.Parameters.AddWithValue("policy_hash", NpgsqlDbType.Text, policyHash);
        command.Parameters.AddWithValue("etag", NpgsqlDbType.Text, etag);
    }

    private static FormPackageDocument ClonePackage(FormPackageDocument package, string formId)
        => new()
        {
            SchemaVersion = package.SchemaVersion,
            FormId = formId,
            Title = package.Title,
            Description = package.Description,
            Target = package.Target,
            Sections = package.Sections,
            Fields = package.Fields,
            SubmitPolicy = package.SubmitPolicy,
            AttachmentPolicy = package.AttachmentPolicy,
            PrivacyPolicy = package.PrivacyPolicy,
            OfflinePolicy = package.OfflinePolicy,
            Provenance = package.Provenance,
            Metadata = package.Metadata
        };

    private static string SerializePackage(FormPackageDocument package)
        => JsonSerializer.Serialize(package, FormPackageJsonContext.Default.FormPackageDocument);

    private static string SerializeValidation(FormPackageValidationResult validation)
        => JsonSerializer.Serialize(validation, FormPackageJsonContext.Default.FormPackageValidationResult);

    private static string HashPolicy(FormPackageDocument package)
    {
        var policyJson = string.Concat(
            JsonSerializer.Serialize(package.SubmitPolicy, FormPackageJsonContext.Default.FormSubmitPolicy),
            JsonSerializer.Serialize(package.AttachmentPolicy, FormPackageJsonContext.Default.FormAttachmentPolicy),
            JsonSerializer.Serialize(package.PrivacyPolicy, FormPackageJsonContext.Default.FormPrivacyPolicy),
            JsonSerializer.Serialize(package.OfflinePolicy, FormPackageJsonContext.Default.FormOfflinePolicy));
        return Hash(policyJson);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string QuoteETag(string hash)
        => $"\"{hash}\"";

    private static string BuildRequestSummaryJson(FormSubmissionRequest request, FormPackageDocument package)
    {
        var privateFields = package.PrivacyPolicy.PrivateFieldIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in package.Fields.Where(static field => field.Private && !string.IsNullOrWhiteSpace(field.FieldId)))
        {
            privateFields.Add(field.FieldId!);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", request.Operation);
            if (request.TargetFeatureId is long targetFeatureId)
            {
                writer.WriteNumber("targetFeatureId", targetFeatureId);
            }

            if (package.PrivacyPolicy.CaptureDeviceId && !string.IsNullOrWhiteSpace(request.ClientId))
            {
                writer.WriteString("clientId", request.ClientId);
            }

            if (request.SubmittedAt is DateTimeOffset submittedAt)
            {
                writer.WriteString("submittedAt", submittedAt.ToString("O", CultureInfo.InvariantCulture));
            }

            writer.WriteStartArray("fieldIds");
            foreach (var fieldId in request.Values.Keys.OrderBy(static fieldId => fieldId, StringComparer.Ordinal))
            {
                writer.WriteStringValue(fieldId);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("privateFieldIds");
            foreach (var fieldId in request.Values.Keys.Where(privateFields.Contains).OrderBy(static fieldId => fieldId, StringComparer.Ordinal))
            {
                writer.WriteStringValue(fieldId);
            }

            writer.WriteEndArray();
            writer.WriteNumber("attachmentCount", request.Attachments.Length);
            writer.WriteBoolean("privacyApplied", privateFields.Count > 0 || package.PrivacyPolicy.RequiredTransformations.Length > 0);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
