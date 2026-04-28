// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Server.Features.Infrastructure.Rendering;

namespace Honua.Server.Features.Infrastructure.Styling.Sld;

/// <summary>
/// <see cref="ISldStyleConverter"/> implementation that delegates to the internal
/// SLD parser and converter. Used by cross-project consumers (e.g. the GeoServer
/// import service in <c>Honua.Postgres</c>) that cannot reference the Honua.Server
/// internal types directly.
/// </summary>
internal sealed class SldStyleConverter : ISldStyleConverter
{
    private static readonly string[] EmptyDocumentErrors = ["SLD document was empty."];

    public SldStyleConversionResult Convert(string sldXml)
    {
        if (string.IsNullOrWhiteSpace(sldXml))
        {
            return new SldStyleConversionResult(
                MapLibreLayersJson: null,
                DetectedSldVersion: null,
                Warnings: Array.Empty<string>(),
                Errors: EmptyDocumentErrors);
        }

        SldDocument document;
        try
        {
            document = SldParser.Parse(sldXml);
        }
        catch (SldParseException ex)
        {
            return new SldStyleConversionResult(
                MapLibreLayersJson: null,
                DetectedSldVersion: null,
                Warnings: Array.Empty<string>(),
                Errors: [ex.Message]);
        }

        var conversion = SldToMapLibreConverter.Convert(document);

        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var diagnostic in conversion.Diagnostics)
        {
            var formatted = FormatDiagnostic(diagnostic);
            if (diagnostic.Severity == SldDiagnosticSeverity.Error)
            {
                errors.Add(formatted);
            }
            else
            {
                warnings.Add(formatted);
            }
        }

        if (conversion.Layers.Length == 0)
        {
            errors.Add("SLD document contained no convertible symbolizers.");
            return new SldStyleConversionResult(
                MapLibreLayersJson: null,
                DetectedSldVersion: conversion.DetectedVersion.ToString(),
                Warnings: warnings,
                Errors: errors);
        }

        var json = JsonSerializer.Serialize(
            conversion.Layers,
            MapLibreStyleJsonContext.Default.MapLibreStyleLayerArray);

        return new SldStyleConversionResult(
            MapLibreLayersJson: json,
            DetectedSldVersion: conversion.DetectedVersion.ToString(),
            Warnings: warnings,
            Errors: errors);
    }

    private static string FormatDiagnostic(SldConversionDiagnostic diagnostic)
    {
        return diagnostic.RuleName is { Length: > 0 } rule
            ? $"[{diagnostic.Construct}] (rule '{rule}') {diagnostic.Message}"
            : $"[{diagnostic.Construct}] {diagnostic.Message}";
    }
}
