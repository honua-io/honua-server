// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Import;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FileFormatsResponse))]
[JsonSerializable(typeof(FilePreview))]
[JsonSerializable(typeof(ImportResult))]
[JsonSerializable(typeof(ImportProgress))]
[JsonSerializable(typeof(ImportLimits))]
[JsonSerializable(typeof(ImportStatus))]
[JsonSerializable(typeof(BackgroundImportResponse))]
[JsonSerializable(typeof(PreviewUrlImportRequest))]
[JsonSerializable(typeof(ImportUrlImportRequest))]
[JsonSerializable(typeof(ImportProgress[]))]
[JsonSerializable(typeof(CancelUploadResponse))]
[JsonSerializable(typeof(CancelImportJobResponse))]
[JsonSerializable(typeof(ActiveUploadsResponse))]
[JsonSerializable(typeof(ActiveImportJobsResponse))]
[JsonSerializable(typeof(UploadProgress[]))]
// New unified progress types
[JsonSerializable(typeof(IOperationProgress))]
[JsonSerializable(typeof(UploadProgress))]
[JsonSerializable(typeof(IngestProgress))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(GeoServicesError))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(DateTimeOffset))]
internal sealed partial class ImportJsonContext : JsonSerializerContext
{
}
