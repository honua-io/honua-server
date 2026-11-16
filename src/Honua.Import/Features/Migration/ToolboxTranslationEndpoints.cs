// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

internal static partial class ToolboxTranslationEndpoints
{
    private const int MaxTools = 200;

    private const string ValidationActivityName = "honua.import.toolbox_translation.validate";

    private const string ValidationOperation = "toolbox-translation-validate";

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

    private static void MarkRejected(Activity? activity, string reason)
    {
        activity?.SetTag(RejectionReasonTag, reason);
        activity?.SetStatus(ActivityStatusCode.Error, reason);
    }

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
