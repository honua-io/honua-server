// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Infrastructure.Rendering;

namespace Honua.Server.Features.Styling.Sld;

internal sealed record SldConversionResult
{
    public required MapLibreStyleLayer[] Layers { get; init; }

    public required SldConversionDiagnostic[] Diagnostics { get; init; }

    public required SldVersion DetectedVersion { get; init; }

    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == SldDiagnosticSeverity.Error);
}

internal sealed record SldExportResult
{
    public required string SldXml { get; init; }

    public required SldConversionDiagnostic[] Diagnostics { get; init; }
}

public sealed record SldConversionDiagnostic
{
    [JsonPropertyName("severity")]
    public required SldDiagnosticSeverity Severity { get; init; }

    [JsonPropertyName("construct")]
    public required string Construct { get; init; }

    // The explanation must not include a raw exception message.
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("ruleName")]
    public string? RuleName { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SldDiagnosticSeverity>))]
public enum SldDiagnosticSeverity
{
    Warning,
    Error
}
