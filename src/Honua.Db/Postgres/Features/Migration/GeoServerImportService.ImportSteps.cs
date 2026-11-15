// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Features.Migration;

/// <summary>
/// Legacy in-place "Import*" step methods used by the non-dry-run, non-apply-plan
/// path of <c>ImportConfigurationAsync</c>. These
/// helpers iterate filtered resource collections, simulate persistence, aggregate
/// per-step results into progress reports, and produce the final
/// <see cref="GeoServerImportResult"/>.
/// </summary>
internal sealed partial class GeoServerImportService
{
    /// <summary>
    /// Invokes the registered <see cref="ISldStyleConverter"/> against the embedded SLD content
    /// when available and returns any warning messages for inclusion in import results.
    /// When conversion errors or missing content combine with <see cref="UnsupportedResourceBehavior.Skip"/>,
    /// <c>shouldSkip</c> is set to <c>true</c> so the caller can abandon the per-style import
    /// without double-counting (<see cref="ImportStepResult.SkippedCount"/> is already incremented).
    /// <c>wasValidated</c> reports whether the converter actually ran and produced no errors;
    /// callers use it to label the imported resource note.
    /// </summary>
    internal List<string> TryConvertSldStyle(
        GeoServerStyleInfo style,
        GeoServerImportRequest request,
        ImportStepResult result,
        out bool converterAvailable,
        out bool shouldSkip,
        out bool wasValidated)
    {
        var warnings = new List<string>();
        converterAvailable = _sldConverter != null;
        shouldSkip = false;
        wasValidated = false;

        if (_sldConverter == null)
        {
            return warnings;
        }

        if (string.IsNullOrWhiteSpace(style.SldContent))
        {
            ApplyUnsupportedStyleBehavior(
                style,
                request,
                result,
                warnings,
                $"SLD style {style.Name} has no embedded content; conversion skipped.",
                out shouldSkip);
            return warnings;
        }

        var conversion = _sldConverter.Convert(style.SldContent!);
        foreach (var warning in conversion.Warnings)
        {
            warnings.Add($"SLD style {style.Name}: {warning}");
        }

        if (conversion.HasErrors)
        {
            var firstError = conversion.Errors.Count > 0 ? conversion.Errors[0] : "SLD conversion produced no layers.";
            ApplyUnsupportedStyleBehavior(
                style,
                request,
                result,
                warnings,
                $"SLD style {style.Name} could not be converted: {firstError}",
                out shouldSkip);
            return warnings;
        }

        wasValidated = true;
        return warnings;
    }

    private void ApplyUnsupportedStyleBehavior(
        GeoServerStyleInfo style,
        GeoServerImportRequest request,
        ImportStepResult result,
        List<string> warnings,
        string message,
        out bool shouldSkip)
    {
        shouldSkip = false;
        var behavior = request.ImportOptions?.UnsupportedStyleBehavior ?? UnsupportedResourceBehavior.LogWarning;

        if (behavior == UnsupportedResourceBehavior.FailImport)
        {
            throw new InvalidOperationException(message);
        }

        if (behavior == UnsupportedResourceBehavior.Skip)
        {
            Log.StyleSkipped(_logger, style.Name);
            result.SkippedCount++;
            shouldSkip = true;
            return;
        }

        Log.StyleRequiresConversion(_logger, style.Name);
        warnings.Add(message);
    }
}
