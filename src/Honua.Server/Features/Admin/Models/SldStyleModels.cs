// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Styling.Sld;

namespace Honua.Server.Features.Admin.Models;

public sealed class SldImportResponse
{
    public string DetectedVersion { get; init; } = string.Empty;

    public int LayerCount { get; init; }

    public JsonElement? MapLibreStyle { get; init; }

    public IReadOnlyList<SldConversionDiagnostic> Diagnostics { get; init; } = Array.Empty<SldConversionDiagnostic>();
}

public sealed class SldImportFailureResponse
{
    public string? DetectedVersion { get; init; }

    public IReadOnlyList<SldConversionDiagnostic> Diagnostics { get; init; } = Array.Empty<SldConversionDiagnostic>();
}
