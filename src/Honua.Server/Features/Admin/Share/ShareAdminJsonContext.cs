// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Share;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShareExportDefinitionRequest))]
[JsonSerializable(typeof(ShareExportDefinitionPageResponse))]
[JsonSerializable(typeof(ShareExportDefinitionResponse))]
[JsonSerializable(typeof(ShareExportRunPageResponse))]
[JsonSerializable(typeof(ShareExportRunResponse))]
[JsonSerializable(typeof(ShareTrafficSummaryResponse))]
[JsonSerializable(typeof(ShareTrafficSeriesResponse))]
[JsonSerializable(typeof(ShareTrafficBucketResponse))]
[JsonSerializable(typeof(ShareTrafficCountsResponse))]
[JsonSerializable(typeof(ShareItemRefResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ShareAdminJsonContext : JsonSerializerContext
{
}
