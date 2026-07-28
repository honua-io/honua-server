// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Import.FileImport;

namespace Honua.Migration;

/// <summary>
/// Admin endpoint for the arcpy/toolbox translation lane (#2145). Accepts an SDK-translated
/// toolbox manifest (<c>.pyt</c>/<c>.tbx</c>/<c>.atbx</c>) and returns the
/// server-authoritative round-trip report against the canonical process catalog.
/// </summary>
/// <remarks>
/// Contract split: the honua-sdk-python <c>honua-migrate</c> scanner parses toolbox sources
/// and proposes per-tool mappings; this endpoint owns the round-trip proof (the catalog is
/// the single source of truth for executable signatures) and the explicit unsupported
/// report. The server never parses toolbox sources and never emulates arcpy execution;
/// translated tools map only to existing native processes invoked through the canonical
/// process/job runtime (OGC API Processes / GPServer).
/// </remarks>
internal static partial class ToolboxTranslationEndpoints
{
    private const int MaxTools = 200;

    /// <summary>
    /// Maps the toolbox translation validation endpoint.
    /// </summary>
    public static void MapToolboxTranslationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/toolbox")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        _ = group.MapPost("/translation/validate", HandleValidateTranslation)
            .WithName("ValidateToolboxTranslation")
            .WithSummary("Validate an SDK-translated toolbox manifest against the canonical process catalog.");
    }

    private static async Task HandleValidateTranslation(HttpContext context)
    {
        ToolboxTranslationManifest? manifest;
        try
        {
            manifest = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.ToolboxTranslationManifest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            await AdminResponseWriter.WriteErrorAsync(
                context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        if (manifest is null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var structuralError = ValidateStructure(manifest);
        if (structuralError is not null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, structuralError, StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<IProcessCatalog>();
        var report = ToolboxTranslationValidator.Validate(Normalize(manifest), catalog);

        Log.TranslationValidated(
            GetLogger(context),
            report.ToolboxName,
            report.SourceFormat,
            report.Summary.ToolCount,
            report.Summary.TranslatedCount,
            report.Summary.PartiallyTranslatedCount,
            report.Summary.UnsupportedCount);

        await Results.Json(report, ImportJsonContext.Default.ToolboxTranslationReport)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static string? ValidateStructure(ToolboxTranslationManifest manifest)
    {
        // Reject a payload that identifies as a different artifact or an unsupported schema
        // version rather than silently reinterpreting it as the v1 toolbox contract. An
        // absent field is treated as "the v1 toolbox manifest": source-generated
        // deserialization leaves omitted properties unset rather than applying the records'
        // initializers, so only an explicitly-supplied incompatible identity is rejected.
        var artifactKind = manifest.ArtifactKind?.Trim();
        if (!string.IsNullOrEmpty(artifactKind)
            && !string.Equals(artifactKind, ToolboxTranslationArtifacts.ManifestKind, StringComparison.Ordinal))
        {
            return $"artifactKind must be '{ToolboxTranslationArtifacts.ManifestKind}'.";
        }

        var artifactVersion = manifest.ArtifactVersion?.Trim();
        if (!string.IsNullOrEmpty(artifactVersion)
            && !ToolboxTranslationArtifacts.SupportedManifestVersions.Contains(artifactVersion))
        {
            return $"artifactVersion must be one of: {string.Join(", ", ToolboxTranslationArtifacts.SupportedManifestVersions)}.";
        }

        if (string.IsNullOrWhiteSpace(manifest.ToolboxName))
        {
            return "toolboxName is required.";
        }

        var format = manifest.SourceFormat?.Trim();
        if (string.IsNullOrEmpty(format)
            || !ToolboxSourceFormats.All.Contains(format.ToLowerInvariant()))
        {
            return $"sourceFormat must be one of: {string.Join(", ", ToolboxSourceFormats.All)}.";
        }

        // An explicit JSON null bypasses the property's non-null default, so guard the
        // reference before any dereference.
        if (manifest.Tools is null || manifest.Tools.Length == 0)
        {
            return "tools is required and must contain at least one tool.";
        }

        if (manifest.Tools.Length > MaxTools)
        {
            return $"tools must contain at most {MaxTools} tools.";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in manifest.Tools)
        {
            if (tool is null || string.IsNullOrWhiteSpace(tool.ToolName))
            {
                return "Every tool requires a non-empty toolName.";
            }

            if (!names.Add(tool.ToolName.Trim()))
            {
                return $"Duplicate toolName '{tool.ToolName.Trim()}' in manifest.";
            }

            // Explicit JSON nulls for the per-tool collections are normalized to empty by
            // Normalize(); a null element inside a present collection is still rejected.
            var invalidMapping = (tool.ParameterMappings ?? []).Any(mapping =>
                mapping is null
                || string.IsNullOrWhiteSpace(mapping.SourceName)
                || string.IsNullOrWhiteSpace(mapping.TargetParameter));

            if (invalidMapping)
            {
                return $"Tool '{tool.ToolName.Trim()}' has a parameter mapping without sourceName or targetParameter.";
            }
        }

        return null;
    }

    /// <summary>
    /// Coerces explicit JSON <c>null</c> collection values (which bypass the records'
    /// non-null defaults during deserialization) to empty arrays so the validator never
    /// dereferences null. A null <c>parameterMappings</c> or <c>unsupportedConstructs</c>
    /// is treated as "none declared".
    /// </summary>
    private static ToolboxTranslationManifest Normalize(ToolboxTranslationManifest manifest) =>
        manifest with
        {
            Tools = [.. manifest.Tools.Select(tool => tool with
            {
                ParameterMappings = tool.ParameterMappings ?? [],
                UnsupportedConstructs = tool.UnsupportedConstructs ?? []
            })]
        };

    private static ILogger<ToolboxTranslationEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<ToolboxTranslationEndpointsLog>>();

    /// <summary>Log category marker for toolbox translation endpoint operations.</summary>
    internal sealed class ToolboxTranslationEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(8100, LogLevel.Warning, "Failed to deserialize toolbox translation request body")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);

        [LoggerMessage(8101, LogLevel.Information, "Validated toolbox translation manifest {ToolboxName} ({SourceFormat}): {ToolCount} tools, {TranslatedCount} translated, {PartiallyTranslatedCount} partial, {UnsupportedCount} unsupported")]
        public static partial void TranslationValidated(
            ILogger logger,
            string toolboxName,
            string sourceFormat,
            int toolCount,
            int translatedCount,
            int partiallyTranslatedCount,
            int unsupportedCount);
    }
}
