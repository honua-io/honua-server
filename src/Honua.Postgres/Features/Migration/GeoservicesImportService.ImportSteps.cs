// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Shared.Models;
using Npgsql;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    /// <inheritdoc />
    public async Task<GeoservicesImportResult> ImportLayerAsync(
        GeoservicesImportRequest request,
        IProgress<GeoservicesImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateTableName(request.TableName);

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? Guid.NewGuid().ToString("N")[..8]
            : request.JobId;
        var startedAt = DateTimeOffset.UtcNow;
        var targetSchema = ResolveTargetSchema(request.TargetSchema);

        Log.ImportStarting(_logger, request.ServiceUrl, request.LayerId, request.TableName);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Phase 1: Discover layer metadata
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Discovering, request,
                "Discovering layer metadata", 0, null);

            var layerInfo = await _restClient.GetLayerInfoAsync(
                request.ServiceUrl,
                request.LayerId,
                request.RequestTimeoutSeconds,
                request.MaxRetries,
                cancellationToken,
                request.Credentials);

            Log.LayerDiscovered(_logger, layerInfo.Name, layerInfo.Fields.Length, layerInfo.FeatureCount);

            var totalFeatures = layerInfo.FeatureCount;
            var batchSize = request.BatchSize ?? layerInfo.MaxRecordCount ?? 1000;

            // Phase 2: Create table
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.CreatingTable, request,
                "Creating PostGIS table", 0, totalFeatures, layerInfo.Name);

            await CreateTableAsync(connection, targetSchema, request.TableName, layerInfo, request.TargetSrid,
                request.OverwriteExisting, cancellationToken);

            // Phase 3: Retrieve and insert features
            var featuresProcessed = 0;
            var failedFeatures = 0;
            var offset = 0;
            var batchNumber = 0;
            var hasMore = true;

            while (hasMore && !cancellationToken.IsCancellationRequested)
            {
                batchNumber++;
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.RetrievingFeatures, request,
                    $"Retrieving batch {batchNumber}", featuresProcessed, totalFeatures, layerInfo.Name);

                // Query features from remote service
                var queryResult = await _restClient.QueryFeaturesAsync(
                    request.ServiceUrl,
                    request.LayerId,
                    offset,
                    batchSize,
                    request.WhereClause,
                    request.OutputFields,
                    request.TargetSrid,
                    request.RequestTimeoutSeconds,
                    request.MaxRetries,
                    cancellationToken,
                    request.Credentials);

                if (queryResult.Features.Length == 0)
                {
                    hasMore = false;
                    break;
                }

                // Insert features into PostGIS
                ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.InsertingFeatures, request,
                    $"Inserting batch {batchNumber} ({queryResult.Features.Length} features)",
                    featuresProcessed, totalFeatures, layerInfo.Name);

                var (inserted, failed) = await InsertFeaturesAsync(
                    connection,
                    targetSchema,
                    request.TableName,
                    layerInfo,
                    queryResult.Features,
                    request.TargetSrid,
                    cancellationToken);

                featuresProcessed += inserted;
                failedFeatures += failed;

                if (failed > 0)
                {
                    warnings.Add($"Batch {batchNumber}: {failed} features failed to insert");
                }

                Log.BatchCompleted(_logger, batchNumber, inserted, failed, featuresProcessed);

                offset += queryResult.Features.Length;
                hasMore = queryResult.ExceededTransferLimit || queryResult.Features.Length == batchSize;
            }

            // Phase 4: Create spatial index
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Publishing, request,
                "Creating spatial index", featuresProcessed, totalFeatures, layerInfo.Name);

            await CreateSpatialIndexAsync(connection, targetSchema, request.TableName, cancellationToken);
            await AnalyzeTableAsync(connection, targetSchema, request.TableName, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            PublishedLayerSummary? publishedLayer = null;
            if (request.AutoPublish)
            {
                publishedLayer = await TryPublishImportedLayerAsync(
                    request,
                    targetSchema,
                    layerInfo,
                    warnings,
                    progress,
                    jobId,
                    startedAt,
                    featuresProcessed,
                    cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();

            Log.ImportCompleted(_logger, request.TableName, featuresProcessed, failedFeatures,
                stopwatch.Elapsed.TotalSeconds);

            // Report final progress
            ReportProgress(progress, jobId, startedAt, GeoservicesImportStatus.Completed, request,
                publishedLayer == null ? "Import completed" : "Import completed and layer published",
                featuresProcessed,
                featuresProcessed,
                layerInfo.Name,
                publishedLayer?.LayerId);

            return GeoservicesImportResult.CreateSuccess(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                featuresProcessed,
                failedFeatures,
                publishedLayer?.LayerId,
                publishedLayer?.ServiceName ?? request.ServiceName,
                layerInfo.Name,
                duration: stopwatch.Elapsed,
                warnings: warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            Log.ImportCancelled(_logger, request.TableName);
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            stopwatch.Stop();
            Log.ImportFailed(_logger, request.TableName, ex);

            return GeoservicesImportResult.CreateFailure(
                request.TableName,
                request.ServiceUrl,
                request.LayerId,
                BuildImportFailureMessage(ex),
                stopwatch.Elapsed);
        }
    }

    private static string BuildImportFailureMessage(Exception exception)
        => exception switch
        {
            ArcGisAuthenticationException auth => auth.Kind switch
            {
                ArcGisAuthenticationFailureKind.CredentialExpired =>
                    $"{ImportCompatibilityCodes.ArcGisTokenExpired}: ArcGIS credentials are expired. Provide a refreshed token or credential reference and retry.",
                ArcGisAuthenticationFailureKind.CredentialDenied =>
                    $"{ImportCompatibilityCodes.ArcGisAccessDenied}: ArcGIS rejected the supplied credentials. Verify access to the layer and retry.",
                _ =>
                    $"{ImportCompatibilityCodes.ArcGisTokenRequired}: ArcGIS service requires authentication. Provide a token or credential reference and retry."
            },
            _ => "Import from ArcGIS service failed."
        };

    private async Task CreateTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        GeoservicesLayerInfo layerInfo,
        int targetSrid,
        bool overwriteExisting,
        CancellationToken cancellationToken)
    {
        if (overwriteExisting)
        {
            await using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} CASCADE";
            await dropCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var createSql = BuildCreateTableSql(schemaName, tableName, layerInfo, targetSrid);
        await using var createCmd = connection.CreateCommand();
        createCmd.CommandText = createSql;
        await createCmd.ExecuteNonQueryAsync(cancellationToken);

        Log.TableCreated(_logger, tableName);
    }

    private static string BuildCreateTableSql(string schemaName, string tableName, GeoservicesLayerInfo layerInfo, int targetSrid)
    {
        var columns = new List<string>
        {
            $"{FieldNames.ObjectId} BIGSERIAL PRIMARY KEY"
        };

        // Add attribute fields
        foreach (var field in layerInfo.Fields)
        {
            if (field.IsObjectId || IsGeometryField(field))
                continue; // We use the canonical objectid primary key instead

            var pgType = MapEsriTypeToPgType(field.Type, field.Length);
            columns.Add($"\"{field.Name.SanitizeFieldName()}\" {pgType}");
        }

        // Add geometry column if the layer has geometry
        if (!string.IsNullOrEmpty(layerInfo.GeometryType))
        {
            var pgGeomType = MapEsriGeometryType(layerInfo.GeometryType);
            columns.Add($"geom geometry({pgGeomType}, {targetSrid})");
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schemaName)};");
        sb.AppendLine(CultureInfo.InvariantCulture, $"CREATE TABLE {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)} (");

        for (var i = 0; i < columns.Count; i++)
        {
            var suffix = i == columns.Count - 1 ? string.Empty : ",";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {columns[i]}{suffix}");
        }

        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string MapEsriTypeToPgType(string esriType, int? length)
    {
        return esriType.ToPostgresType(length);
    }

    private static string MapEsriGeometryType(string esriGeometryType)
    {
        return esriGeometryType.ToUpperInvariant() switch
        {
            "ESRIGEOMETRYPOINT" => "POINT",
            "ESRIGEOMETRYMULTIPOINT" => "MULTIPOINT",
            "ESRIGEOMETRYPOLYLINE" => "MULTILINESTRING",
            "ESRIGEOMETRYPOLYGON" => "MULTIPOLYGON",
            "ESRIGEOMETRYENVELOPE" => "POLYGON",
            _ => "GEOMETRY"
        };
    }

    private static bool IsGeometryField(GeoservicesFieldInfo field)
        => field.Type.Equals("esriFieldTypeGeometry", StringComparison.OrdinalIgnoreCase);
}
