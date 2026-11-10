// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Geoprocessing.Diagnostics;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GpDiagnosticEvidence))]
[JsonSerializable(typeof(GpDiagnosticReleaseIdentity))]
[JsonSerializable(typeof(GpDiagnosticRuntimeIdentity))]
[JsonSerializable(typeof(GpDiagnosticDependency))]
[JsonSerializable(typeof(GpDiagnosticError))]
[JsonSerializable(typeof(GpDiagnosticEvent))]
[JsonSerializable(typeof(GpDiagnosticAttribute))]
[JsonSerializable(typeof(GpDiagnosticInput))]
[JsonSerializable(typeof(GpDiagnosticArtifact))]
[JsonSerializable(typeof(GpDiagnosticInputExport))]
[JsonSerializable(typeof(GpDiagnosticIncludedInput))]
internal sealed partial class GpDiagnosticBundleJsonContext : JsonSerializerContext
{
}
