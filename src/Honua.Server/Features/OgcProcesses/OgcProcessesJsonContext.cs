// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcProcesses.Models;

namespace Honua.Server.Features.OgcProcesses;

/// <summary>
/// Source-generated JSON serializer context for OGC API Processes models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LandingPage))]
[JsonSerializable(typeof(ConformanceDeclaration))]
[JsonSerializable(typeof(Link))]
[JsonSerializable(typeof(ImmutableArray<Link>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(OgcProcessSummary))]
[JsonSerializable(typeof(OgcProcessList))]
[JsonSerializable(typeof(OgcProcessDescription))]
[JsonSerializable(typeof(OgcProcessIoDescription))]
[JsonSerializable(typeof(OgcProcessIoSchema))]
[JsonSerializable(typeof(ImmutableDictionary<string, OgcProcessIoDescription>))]
[JsonSerializable(typeof(ImmutableArray<OgcProcessSummary>))]
[JsonSerializable(typeof(OgcExecuteRequest))]
[JsonSerializable(typeof(ImmutableDictionary<string, JsonElement>))]
[JsonSerializable(typeof(OgcStatusInfo))]
[JsonSerializable(typeof(OgcProcessError))]
[JsonSerializable(typeof(OgcResultsDocument))]
[JsonSerializable(typeof(OgcStatusInfo[]))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
internal sealed partial class OgcProcessesJsonContext : JsonSerializerContext
{
}
