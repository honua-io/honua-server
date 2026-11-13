// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Migration;

// The stored form of a progress snapshot: its type name beside its data, so a reader can rebuild the right record.
internal static class OperationProgressSerializer
{
    public static string Serialize(IOperationProgress progress)
    {
        var wrapper = new ProgressWrapper
        {
            Type = progress.Type,
            ProgressType = progress.GetType().Name,
            Data = SerializeData(progress)
        };

        return JsonSerializer.Serialize(wrapper, UniversalProgressJsonContext.Default.ProgressWrapper);
    }

    public static IOperationProgress? Deserialize(string json)
    {
        var wrapper = JsonSerializer.Deserialize(json, UniversalProgressJsonContext.Default.ProgressWrapper);
        if (wrapper?.Data == null)
        {
            return null;
        }

        return wrapper.ProgressType switch
        {
            nameof(ImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.ImportProgress),
            nameof(GeoservicesImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.GeoservicesImportProgress),
            nameof(GeoServerImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.GeoServerImportProgress),
            nameof(UploadProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.UploadProgress),
            nameof(IngestProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.IngestProgress),
            nameof(TileOperationProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.TileOperationProgress),
            nameof(ExportProgress) => DeserializeExportProgress(wrapper.Data),
            nameof(PrintProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.PrintProgress),
            nameof(RasterImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.RasterImportProgress),
            nameof(GeoprocessingProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.GeoprocessingProgress),
            nameof(PublishingProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.PublishingProgress),
            nameof(WorkflowProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.WorkflowProgress),
            nameof(DeploymentProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.DeploymentProgress),
            nameof(TemporalCorrectiveJobProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.TemporalCorrectiveJobProgress),
            nameof(LayerPublishJobProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.LayerPublishJobProgress),
            _ => null
        };
    }

    private static ExportProgress? DeserializeExportProgress(JsonElement data)
    {
        try
        {
            return data.Deserialize(UniversalProgressJsonContext.Default.ExportProgress);
        }
        catch (JsonException exception)
        {
            throw new InvalidPersistedExportProgressException(exception);
        }
    }

    private static JsonElement SerializeData(IOperationProgress progress)
        => progress switch
        {
            ImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.ImportProgress),
            GeoservicesImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.GeoservicesImportProgress),
            GeoServerImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.GeoServerImportProgress),
            UploadProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.UploadProgress),
            IngestProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.IngestProgress),
            TileOperationProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.TileOperationProgress),
            ExportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.ExportProgress),
            PrintProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.PrintProgress),
            RasterImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.RasterImportProgress),
            GeoprocessingProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.GeoprocessingProgress),
            PublishingProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.PublishingProgress),
            WorkflowProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.WorkflowProgress),
            DeploymentProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.DeploymentProgress),
            TemporalCorrectiveJobProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.TemporalCorrectiveJobProgress),
            LayerPublishJobProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.LayerPublishJobProgress),
            _ => throw new NotSupportedException($"Unsupported progress type '{progress.GetType().FullName}'.")
        };

    public static bool SameSnapshot(IOperationProgress current, IOperationProgress expected)
    {
        if (current.GetType() != expected.GetType())
        {
            return false;
        }

        // Duration is a computed wall-clock view, never a state field, so it is not compared.
        var currentData = SerializeData(current);
        var expectedData = SerializeData(expected);
        foreach (var property in expectedData.EnumerateObject())
        {
            if (property.NameEquals("Duration"))
            {
                continue;
            }
            if (!currentData.TryGetProperty(property.Name, out var value) ||
                !string.Equals(value.GetRawText(), property.Value.GetRawText(), StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
