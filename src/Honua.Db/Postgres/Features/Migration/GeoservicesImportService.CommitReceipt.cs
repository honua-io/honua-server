// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private async Task<GeoservicesImportResult?> ReadCommittedImportAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, GeoservicesImportRequest request,
        string schema, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            return null;
        }

        var table = SchemaSearchPath.QualifyTable("remote_import_commit_receipt", _schemaConfiguration.MetadataSchema);
        await using var command = new NpgsqlCommand($"""
            SELECT schema_name, table_name, source_service_url, source_layer_id, feature_count, failed_features
            FROM {table} WHERE job_id = @job
            """, connection, transaction);
        command.Parameters.AddWithValue("job", request.JobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (reader.GetString(0) != schema || reader.GetString(1) != request.TableName ||
            reader.GetString(2) != request.ServiceUrl || reader.GetInt32(3) != request.LayerId)
        {
            throw new InvalidOperationException("Import job identity does not match its committed target and source.");
        }

        return new GeoservicesImportResult
        {
            Success = false, NeedsReview = true, DataCommitted = true,
            TableName = request.TableName, SourceServiceUrl = request.ServiceUrl,
            SourceLayerId = request.LayerId, ServiceName = request.ServiceName,
            FeatureCount = reader.GetInt32(4), FailedFeatures = reader.GetInt32(5),
            FidelityVerdict = MigrationFidelityVerdicts.Incomplete,
            ErrorMessage = "Imported rows were committed by an earlier execution; publication, attachments and reconciliation require review."
        };
    }

    private async Task RecordImportCommitAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, GeoservicesImportRequest request,
        string schema, int features, int failures, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobId))
        {
            return;
        }

        var table = SchemaSearchPath.QualifyTable("remote_import_commit_receipt", _schemaConfiguration.MetadataSchema);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {table} (job_id, schema_name, table_name, source_service_url, source_layer_id, feature_count, failed_features)
            VALUES (@job, @schema, @table, @source, @layer, @features, @failures)
            """, connection, transaction);
        command.Parameters.AddWithValue("job", request.JobId);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", request.TableName);
        command.Parameters.AddWithValue("source", request.ServiceUrl);
        command.Parameters.AddWithValue("layer", request.LayerId);
        command.Parameters.AddWithValue("features", features);
        command.Parameters.AddWithValue("failures", failures);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
