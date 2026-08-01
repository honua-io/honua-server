// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Import.FileImport;
using Honua.ServiceDefaults;
using Microsoft.Net.Http.Headers;

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

    /// <summary>Span name for a toolbox translation validation, on the shared Honua source.</summary>
    private const string ValidationActivityName = "honua.import.toolbox_translation.validate";

    private const string ValidationOperation = "toolbox-translation-validate";

    /// <summary>The only media type this endpoint accepts a manifest in.</summary>
    private const string JsonMediaType = "application/json";

    // Span attributes. Counts and the source format are low-cardinality; the rejection
    // attribute carries a fixed reason CODE rather than the caller-facing message, which
    // interpolates manifest content (tool names) and would be unbounded as a tag value.
    private const string SourceFormatTag = "honua.import.toolbox.source_format";
    private const string ToolCountTag = "honua.import.toolbox.tool_count";
    private const string TranslatedCountTag = "honua.import.toolbox.translated_count";
    private const string PartiallyTranslatedCountTag = "honua.import.toolbox.partially_translated_count";
    private const string UnsupportedCountTag = "honua.import.toolbox.unsupported_count";
    private const string RejectionReasonTag = "honua.import.toolbox.rejection_reason";

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
        // A log line alone leaves a clean validation and a degraded one indistinguishable in
        // OpenTelemetry, so the outcome is also carried on a span: the translated/partial/
        // unsupported counts on success, and a stable rejection code on every early return,
        // so a manifest the server refuses shows up as an error span rather than no span.
        using var activity = HonuaTelemetry.StartActivity(ValidationActivityName);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, ValidationOperation);

        // A non-JSON Content-Type is a MEDIA TYPE problem, not a malformed body:
        // ReadFromJsonAsync refuses it before deserialization ever runs, and the generic catch
        // below would report a perfectly well-formed payload as "Invalid request body". Check it
        // up front so the caller gets 415 and knows what to change (honua-server#2145 review).
        if (!HasJsonContentType(context.Request))
        {
            MarkRejected(activity, "unsupported-media-type");
            await AdminResponseWriter.WriteErrorAsync(
                context,
                $"Content-Type must be '{JsonMediaType}'.",
                StatusCodes.Status415UnsupportedMediaType);
            return;
        }

        // The body is parsed as a document first so artifact identity can be judged on what
        // the caller actually sent: an omitted property is not distinguishable from an
        // explicit null once deserialized onto the manifest's non-nullable properties.
        JsonDocument? document;
        ToolboxTranslationManifest? manifest;
        try
        {
            document = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.JsonDocument,
                context.RequestAborted).ConfigureAwait(false);
            manifest = document?.Deserialize(ImportJsonContext.Default.ToolboxTranslationManifest);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // An oversized body surfaces as BadHttpRequestException with status 413; let it
        // reach the shared ExceptionMapper, which maps it to a clean 413 envelope. Catching
        // it here would downgrade the response to 400 and lose those semantics.
        catch (BadHttpRequestException)
        {
            throw;
        }
        // Intentionally generic: reading/deserializing can throw JsonException,
        // NotSupportedException, or IOException for malformed/unreadable request bodies;
        // map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
            MarkRejected(activity, "malformed-body");
            await AdminResponseWriter.WriteErrorAsync(
                context, "Invalid request body.", StatusCodes.Status400BadRequest);
            return;
        }

        using var bodyDocument = document;
        if (manifest is null || bodyDocument is null || bodyDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            MarkRejected(activity, "missing-body");
            await AdminResponseWriter.WriteErrorAsync(
                context, "Request body is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var identityError = ValidateArtifactIdentity(bodyDocument.RootElement);
        if (identityError is not null)
        {
            MarkRejected(activity, "artifact-identity");
            await AdminResponseWriter.WriteErrorAsync(
                context, identityError, StatusCodes.Status400BadRequest);
            return;
        }

        var structuralError = ValidateStructure(manifest);
        if (structuralError is not null)
        {
            MarkRejected(activity, "invalid-structure");
            await AdminResponseWriter.WriteErrorAsync(
                context, structuralError, StatusCodes.Status400BadRequest);
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<IProcessCatalog>();
        var conditionalInputProbe = context.RequestServices.GetService<IProcessConditionalInputProbe>();
        var report = ToolboxTranslationValidator.Validate(Normalize(manifest), catalog, conditionalInputProbe);

        activity?.SetTag(SourceFormatTag, report.SourceFormat);
        activity?.SetTag(ToolCountTag, report.Summary.ToolCount);
        activity?.SetTag(TranslatedCountTag, report.Summary.TranslatedCount);
        activity?.SetTag(PartiallyTranslatedCountTag, report.Summary.PartiallyTranslatedCount);
        activity?.SetTag(UnsupportedCountTag, report.Summary.UnsupportedCount);

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

    /// <summary>
    /// True when the request declares a JSON body. Mirrors what
    /// <c>HttpRequestJsonExtensions.ReadFromJsonAsync</c> accepts: the JSON media type or any
    /// <c>+json</c> structured suffix, with an optional charset parameter. An ABSENT
    /// Content-Type is accepted for compatibility with the existing empty-body path, which
    /// answers 400 "Request body is required."
    /// </summary>
    private static bool HasJsonContentType(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ContentType))
        {
            return true;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
        {
            return false;
        }

        return mediaType.MediaType.Equals(JsonMediaType, StringComparison.OrdinalIgnoreCase)
            || mediaType.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records a refused manifest on the span with a stable reason code. The caller-facing
    /// message is deliberately not used: it interpolates manifest content, so it belongs in
    /// the response body rather than in an unbounded span attribute.
    /// </summary>
    private static void MarkRejected(Activity? activity, string reason)
    {
        activity?.SetTag(RejectionReasonTag, reason);
        activity?.SetStatus(ActivityStatusCode.Error, reason);
    }

    /// <summary>
    /// Rejects a payload that identifies as a different artifact or an unsupported schema
    /// version instead of silently reinterpreting it under the v1 toolbox contract. Judged
    /// on the raw document so an <em>omitted</em> identity (accepted as v1) stays
    /// distinguishable from an explicit <c>null</c>/blank one (rejected).
    /// </summary>
    private static string? ValidateArtifactIdentity(JsonElement root)
    {
        if (root.TryGetProperty("artifactKind", out var kindElement)
            && (kindElement.ValueKind != JsonValueKind.String
                || !string.Equals(
                    kindElement.GetString()?.Trim(),
                    ToolboxTranslationArtifacts.ManifestKind,
                    StringComparison.Ordinal)))
        {
            return $"artifactKind must be '{ToolboxTranslationArtifacts.ManifestKind}'.";
        }

        if (root.TryGetProperty("artifactVersion", out var versionElement)
            && (versionElement.ValueKind != JsonValueKind.String
                || !ToolboxTranslationArtifacts.SupportedManifestVersions.Contains(
                    versionElement.GetString()?.Trim() ?? string.Empty)))
        {
            return $"artifactVersion must be one of: {string.Join(", ", ToolboxTranslationArtifacts.SupportedManifestVersions)}.";
        }

        return null;
    }

    private static string? ValidateStructure(ToolboxTranslationManifest manifest)
    {
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
