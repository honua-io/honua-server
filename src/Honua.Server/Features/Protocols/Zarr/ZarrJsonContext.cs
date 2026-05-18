// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Protocols.Zarr.Models;

namespace Honua.Server.Features.Protocols.Zarr;

/// <summary>
/// AOT-safe JSON serialization context for Zarr admin endpoints.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegisterZarrRequest))]
[JsonSerializable(typeof(ZarrRegistrationResponse))]
[JsonSerializable(typeof(ZarrRegistrationResponse[]))]
[JsonSerializable(typeof(ZarrVariableSummary))]
[JsonSerializable(typeof(ZarrVariableSummary[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
internal sealed partial class ZarrJsonContext : JsonSerializerContext
{
}
