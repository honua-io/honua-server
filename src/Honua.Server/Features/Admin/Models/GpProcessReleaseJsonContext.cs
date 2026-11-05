// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(GpProcessReleaseJobRequest))]
[JsonSerializable(typeof(GpProcessReleaseJobResponse))]
[JsonSerializable(typeof(IReadOnlyList<GpProcessReleaseJobScope>))]
[JsonSerializable(typeof(GpProcessReleaseResponse))]
[JsonSerializable(typeof(GpProcessReleaseListResponse))]
[JsonSerializable(typeof(GpProcessBindingTargetResponse))]
[JsonSerializable(typeof(GpProcessBindingResponse))]
[JsonSerializable(typeof(GpProcessBindingListResponse))]
[JsonSerializable(typeof(GpProcessBindingTargetRequest))]
[JsonSerializable(typeof(GpProcessBindingActivateRequest))]
[JsonSerializable(typeof(GpProcessBindingRefusedResponse))]
internal sealed partial class GpProcessReleaseJsonContext : JsonSerializerContext
{
}
