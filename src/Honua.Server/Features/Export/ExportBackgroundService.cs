// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Threading.Channels;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Export.Writers;
using Honua.Server.Features.Infrastructure.Progress;

namespace Honua.Server.Features.Export;

/// <summary>
/// Background service that processes queued export jobs for large datasets.
/// </summary>
internal sealed class ExportBackgroundService : BackgroundService
{
    private readonly Channel<ExportJob> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExportBackgroundService> _logger;

    public ExportBackgroundService(
        Channel<ExportJob> channel,
        IServiceScopeFactory scopeFactory,
        ILogger<ExportBackgroundService> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ExportLog.AsyncExportFailed(_logger, job.JobId, ex);
            }
        }
    }

    private async Task ProcessJobAsync(ExportJob job, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var progressStore = scope.ServiceProvider.GetRequiredService<IUniversalProgressStore>();
        var streamingStore = scope.ServiceProvider.GetRequiredService<IStreamingFeatureStore>();
        var crsRegistry = scope.ServiceProvider.GetRequiredService<ICrsRegistry>();
        var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();

        // Retrieve the queued progress (preserves original StartedAt) and advance to processing
        var existing = await progressStore.GetProgressAsync(job.JobId, cancellationToken);
        var progress = existing is ExportProgress queued
            ? queued with { Status = OperationStatus.Processing, CurrentPhase = "Exporting features" }
            : ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, job.LayerId, job.TotalFeatures) with
            {
                Status = OperationStatus.Processing,
                CurrentPhase = "Exporting features"
            };
        await progressStore.SetProgressAsync(job.JobId, progress, TimeSpan.FromHours(24), cancellationToken);

        var scratchDir = Path.Combine(Path.GetTempPath(), "honua-export", job.JobId);
        Directory.CreateDirectory(scratchDir);
        var sw = Stopwatch.StartNew();

        try
        {
            var features = streamingStore.StreamFeaturesAsync(job.LayerId, job.Query, cancellationToken);
            var outputFile = await WriteToFileAsync(job, features, scratchDir, crsRegistry, _logger, cancellationToken);
            var fileInfo = new FileInfo(outputFile);

            // Upload to cloud storage and generate presigned download URL
            await using var fileStream = File.OpenRead(outputFile);
            var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
            {
                Content = fileStream,
                FileName = Path.GetFileName(outputFile),
                ContentType = GetContentType(job.Format),
                Folder = "exports",
                TimeToLive = TimeSpan.FromHours(24)
            }, cancellationToken);

            var downloadUrl = uploadResult.File is not null
                ? await cloudStorage.GetPresignedUrlAsync(
                    uploadResult.File.FileId, TimeSpan.FromHours(24), cancellationToken)
                : null;

            var completed = progress with
            {
                Status = OperationStatus.Completed,
                ProcessedFeatures = job.TotalFeatures,
                OutputSizeBytes = fileInfo.Length,
                DownloadUrl = downloadUrl,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Export completed"
            };
            await progressStore.SetProgressAsync(job.JobId, completed, TimeSpan.FromHours(24), cancellationToken);

            ExportLog.AsyncExportCompleted(_logger, job.JobId, job.TotalFeatures, fileInfo.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExportLog.AsyncExportFailed(_logger, job.JobId, ex);

            try
            {
                var failed = progress with
                {
                    Status = OperationStatus.Failed,
                    ErrorMessage = ex.Message,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Export failed"
                };
                await progressStore.SetProgressAsync(job.JobId, failed, TimeSpan.FromHours(24), CancellationToken.None);
            }
            catch (Exception progressEx)
            {
                _logger.LogWarning(progressEx,
                    "Failed to persist error status for export job {JobId} (original error: {OriginalError})",
                    job.JobId, ex.Message);
            }
        }
        finally
        {
            try { Directory.Delete(scratchDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private static async Task<string> WriteToFileAsync(
        ExportJob job,
        IAsyncEnumerable<Feature> features,
        string scratchDir,
        ICrsRegistry crsRegistry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? srsName = null;
        string? srsWkt = null;
        var crs = await crsRegistry.ResolveBySridAsync(job.OutputSrid, cancellationToken);
        if (crs.HasValue)
        {
            srsName = $"EPSG:{job.OutputSrid}";
            srsWkt = crs.Value.Wkt;
        }

        var baseName = ExportEndpoints.SanitizeExportFilename(job.ServiceName, job.LayerName);

        switch (job.Format.ToLowerInvariant())
        {
            case "csv":
                {
                    var csvPath = Path.Combine(scratchDir, $"{baseName}.csv");
                    await using var stream = File.Create(csvPath);
                    await CsvExportWriter.WriteAsync(stream, features, job.Fields, cancellationToken);
                    return csvPath;
                }
            case "shapefile":
                {
                    var zipPath = Path.Combine(scratchDir, $"{baseName}.zip");
                    await using var stream = File.Create(zipPath);
                    await ShapefileExportWriter.WriteAsync(
                        stream, features, job.Fields,
                        job.GeometryType,
                        srsWkt,
                        logger,
                        cancellationToken);
                    return zipPath;
                }
            case "gpkg":
                {
                    var gpkgPath = Path.Combine(scratchDir, $"{baseName}.gpkg");
                    await GeoPackageExportWriter.WriteAsync(
                        gpkgPath, features, job.Fields,
                        job.GeometryType,
                        job.OutputSrid, srsName, srsWkt, cancellationToken);
                    return gpkgPath;
                }
            default:
                throw new InvalidOperationException($"Unsupported export format: {job.Format}");
        }
    }

    private static string GetContentType(string format) => format.ToLowerInvariant() switch
    {
        "csv" => "text/csv",
        "shapefile" => "application/zip",
        "gpkg" => "application/geopackage+sqlite3",
        _ => "application/octet-stream"
    };
}
