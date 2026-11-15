// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private sealed record ImportCommitReceiptContext(
        NpgsqlConnection Connection, NpgsqlTransaction Transaction, GeoservicesImportRequest Request,
        string Schema, int Features = 0, int Failures = 0);

    private async Task<GeoservicesImportResult?> GetTargetLockFailureAsync(
        ImportCommitReceiptContext context, System.Diagnostics.Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        if (await TryAcquireTargetImportLockAsync(context.Connection, context.Schema, context.Request.TableName, cancellationToken))
        {
            return null;
        }

        await context.Transaction.RollbackAsync(CancellationToken.None);
        stopwatch.Stop();
        var request = context.Request;
        Log.TargetImportInProgress(_logger, request.TableName);
        return GeoservicesImportResult.CreateFailure(
            request.TableName, request.ServiceUrl, request.LayerId,
            $"Another import is already writing target table '{request.TableName}'; this job did not modify it. "
            + "Wait for that import to finish, then retry.", stopwatch.Elapsed) with
        {
            ServiceName = request.ServiceName
        };
    }

    private async Task<GeoservicesImportResult?> ReadCommittedImportAsync(
        ImportCommitReceiptContext context, CancellationToken cancellationToken)
    {
        var (connection, transaction, request, schema, _, _) = context;
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

    private async Task PrepareImportCommitAsync(ImportCommitReceiptContext context, GeoservicesLayerInfo layerInfo, CancellationToken cancellationToken)
    {
        // Nonspatial source tables have no geometry column to index.
        if (!string.IsNullOrEmpty(layerInfo.GeometryType))
        {
            await CreateSpatialIndexAsync(context.Connection, context.Schema, context.Request.TableName, cancellationToken);
        }

        await AnalyzeTableAsync(context.Connection, context.Schema, context.Request.TableName, cancellationToken);
        await RecordImportCommitAsync(context, cancellationToken);
        await JoinCommitAsync(context.Request.CommitParticipant, new ImportCommitScope
        {
            Transaction = context.Transaction,
            Schema = context.Schema,
            PhysicalTable = context.Request.TableName,
            FeatureCount = context.Features
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordImportCommitAsync(ImportCommitReceiptContext context, CancellationToken cancellationToken)
    {
        var (connection, transaction, request, schema, features, failures) = context;
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
