// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Server.Features.Import;
using Honua.Server.Features.Migration;
using Honua.Server.Features.FileImport;
using Honua.Server.Features.RasterImport;

namespace Honua.Server.Features.Migration;

/// <summary>
/// Unified migration source scanner endpoints.
/// </summary>
internal static class MigrationScannerEndpoints
{
    /// <summary>
    /// Maps migration source scanner endpoints.
    /// </summary>
    public static void MapMigrationScannerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Import")
            .RequireAdminAuthorization();

        _ = group.MapPost("/scan", HandleScanSource)
            .WithName("ScanMigrationSource")
            .WithSummary("Scan a supported migration source and return a deterministic inventory artifact");
    }

    private static async Task HandleScanSource(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        MigrationInventoryScanRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                ImportJsonContext.Default.MigrationInventoryScanRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (!TryNormalizeSourceKind(request.SourceKind, out var sourceKind))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "SourceKind must be one of: geoserver, geoserver-rest, geoservices, arcgis-geoservices-rest, ogc-api-features, ogc-wfs, ogc-wms, ogc-wmts, ogc-wcs, ogc-api-coverages.",
                StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "SourceUrl is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (request.TimeoutSeconds is <= 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "TimeoutSeconds must be greater than 0.", StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            MigrationSourceInventoryArtifact artifact;

            switch (sourceKind)
            {
                case "geoserver-rest":
                    {
                        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
                        var validation = await GeoServerServiceUrlValidation.ValidateAsync(
                            request.SourceUrl,
                            allowUnsafeLocalUrls,
                            cancellationToken).ConfigureAwait(false);
                        if (!validation.IsValid)
                        {
                            await AdminResponseWriter.WriteErrorAsync(
                                context,
                                validation.ErrorMessage!,
                                StatusCodes.Status400BadRequest);
                            return;
                        }

                        var importService = context.RequestServices.GetRequiredService<IGeoServerImportService>();
                        artifact = await importService.ScanSourceAsync(
                            new GeoServerDiscoveryRequest
                            {
                                GeoServerRestUrl = request.SourceUrl,
                                Username = request.Username,
                                Password = request.Password,
                                TimeoutSeconds = request.TimeoutSeconds ?? 120,
                                IncludeCompatibilityAnalysis = true,
                                IncludeStyleContent = request.IncludeStyleContent ?? false,
                                AllowUnsafeLocalUrls = allowUnsafeLocalUrls
                            },
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case "ogc-wfs":
                case "ogc-wms":
                case "ogc-wmts":
                case "ogc-wcs":
                case "ogc-api-coverages":
                    {
                        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
                        var validation = await OgcServiceUrlValidation.ValidateAsync(
                            request.SourceUrl,
                            allowUnsafeLocalUrls,
                            cancellationToken).ConfigureAwait(false);
                        if (!validation.IsValid)
                        {
                            await AdminResponseWriter.WriteErrorAsync(
                                context,
                                validation.ErrorMessage!,
                                StatusCodes.Status400BadRequest);
                            return;
                        }

                        var scanner = context.RequestServices.GetRequiredService<IOgcServiceMigrationScanner>();
                        artifact = await scanner.ScanSourceAsync(
                            new OgcServiceScanRequest
                            {
                                ServiceType = ToOgcServiceType(sourceKind),
                                ServiceUrl = request.SourceUrl,
                                Version = request.ServiceVersion,
                                TimeoutSeconds = request.TimeoutSeconds ?? 60,
                                AllowUnsafeLocalUrls = allowUnsafeLocalUrls
                            },
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case "ogc-api-features":
                    {
                        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
                        var validation = await OgcServiceUrlValidation.ValidateAsync(
                            request.SourceUrl,
                            allowUnsafeLocalUrls,
                            cancellationToken).ConfigureAwait(false);
                        if (!validation.IsValid)
                        {
                            await AdminResponseWriter.WriteErrorAsync(
                                context,
                                validation.ErrorMessage!,
                                StatusCodes.Status400BadRequest);
                            return;
                        }

                        var scanner = context.RequestServices.GetRequiredService<IOgcApiFeaturesMigrationScanner>();
                        artifact = await scanner.ScanSourceAsync(
                            new OgcApiFeaturesScanRequest
                            {
                                ServiceUrl = request.SourceUrl,
                                TimeoutSeconds = request.TimeoutSeconds ?? 60,
                                AllowUnsafeLocalUrls = allowUnsafeLocalUrls
                            },
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                default:
                    {
                        var validation = await GeoservicesServiceUrlValidation.ValidateAsync(
                            request.SourceUrl,
                            cancellationToken).ConfigureAwait(false);
                        if (!validation.IsValid)
                        {
                            await AdminResponseWriter.WriteErrorAsync(
                                context,
                                validation.ErrorMessage!,
                                StatusCodes.Status400BadRequest);
                            return;
                        }

                        var importService = context.RequestServices.GetRequiredService<IGeoservicesImportService>();
                        var credentialValidationError = GeoservicesCredentialResolution.ValidateDiscoveryCredentialRequest(
                            request.Credentials,
                            context.RequestServices);
                        if (credentialValidationError != null)
                        {
                            await AdminResponseWriter.WriteErrorAsync(
                                context,
                                credentialValidationError,
                                StatusCodes.Status400BadRequest);
                            return;
                        }

                        var scanRequest = await GeoservicesCredentialResolution.ResolveSecretReferencesAsync(
                            new GeoservicesDiscoveryRequest
                            {
                                ServiceUrl = request.SourceUrl,
                                TimeoutSeconds = request.TimeoutSeconds ?? 30,
                                Credentials = request.Credentials
                            },
                            context.RequestServices,
                            cancellationToken).ConfigureAwait(false);

                        artifact = await importService.ScanSourceAsync(
                            scanRequest,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
            }

            if (IsJsonExportRequested(context.Request.Query))
            {
                await WriteIndentedJsonAttachmentAsync(
                    context,
                    CreateScanResponse(artifact, request),
                    artifact.Source.DisplayName,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var response = CreateScanResponse(artifact, request);
                if (response is MigrationScanArtifactSet artifactSet)
                {
                    await Results.Json(artifactSet, ImportJsonContext.Default.MigrationScanArtifactSet)
                        .ExecuteAsync(context).ConfigureAwait(false);
                }
                else
                {
                    await Results.Json(artifact, ImportJsonContext.Default.MigrationSourceInventoryArtifact)
                        .ExecuteAsync(context).ConfigureAwait(false);
                }
            }
        }
        catch (HttpRequestException)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Failed to connect to source service.",
                StatusCodes.Status502BadGateway);
        }
        catch (SecretNotFoundException)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Failed to resolve GeoServices credential secret reference.",
                StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("secret reference", StringComparison.OrdinalIgnoreCase))
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Failed to resolve GeoServices credential secret reference.",
                StatusCodes.Status400BadRequest);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Source scan timed out.",
                StatusCodes.Status504GatewayTimeout);
        }
    }

    private static bool TryNormalizeSourceKind(string? sourceKind, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceKind))
        {
            return false;
        }

        normalized = sourceKind.Trim().ToLowerInvariant() switch
        {
            "geoserver" or "geoserver-rest" => "geoserver-rest",
            "geoservices" or "arcgis-geoservices-rest" => "arcgis-geoservices-rest",
            "ogc-api" or "ogc-api-features" or "oapif" => "ogc-api-features",
            "wfs" or "ogc-wfs" => "ogc-wfs",
            "wms" or "ogc-wms" => "ogc-wms",
            "wmts" or "ogc-wmts" => "ogc-wmts",
            "wcs" or "ogc-wcs" => "ogc-wcs",
            "coverages" or "ogc-coverages" or "ogc-api-coverages" => "ogc-api-coverages",
            _ => string.Empty
        };

        return normalized.Length > 0;
    }

    private static string ToOgcServiceType(string sourceKind)
        => sourceKind switch
        {
            "ogc-api-coverages" => "OGC API Coverages",
            _ => sourceKind["ogc-".Length..]
        };

    private static object CreateScanResponse(
        MigrationSourceInventoryArtifact artifact,
        MigrationInventoryScanRequest request)
    {
        if (!string.Equals(request.ArtifactSet, "all", StringComparison.OrdinalIgnoreCase))
        {
            return artifact;
        }

        var manifest = MigrationManifestTranslator.Translate(artifact, new MigrationManifestTranslationOptions
        {
            TargetServiceName = request.TargetServiceName
        });
        var parityEvidence = MigrationParityEvidenceGenerator.Generate(artifact, manifest);
        return new MigrationScanArtifactSet
        {
            Inventory = artifact,
            Manifest = manifest,
            ParityEvidence = parityEvidence
        };
    }

    private static bool IsJsonExportRequested(IQueryCollection query)
    {
        if (!query.TryGetValue("export", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteIndentedJsonAttachmentAsync(
        HttpContext context,
        object response,
        string? displayName,
        MigrationInventoryScanRequest request,
        CancellationToken cancellationToken)
    {
        var fileSlug = SanitizeFilenameSlug(displayName);
        var filename = string.Equals(request.ArtifactSet, "all", StringComparison.OrdinalIgnoreCase)
            ? $"{fileSlug}-migration-artifacts.json"
            : $"{fileSlug}-inventory.json";

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{filename}\"";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var buffer = new ArrayBufferWriter<byte>();
        await using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions { Indented = true }))
        {
            JsonSerializer.Serialize(
                writer,
                response,
                response is MigrationScanArtifactSet
                    ? ImportJsonContext.Default.MigrationScanArtifactSet
                    : ImportJsonContext.Default.MigrationSourceInventoryArtifact);
        }

        await context.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    internal static string SanitizeFilenameSlug(string? candidate)
    {
        const int MaxLength = 64;
        const string Fallback = "inventory";

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Fallback;
        }

        var builder = new StringBuilder(candidate.Length);
        var lastWasSeparator = false;

        foreach (var ch in candidate)
        {
            if ((ch >= 'a' && ch <= 'z') ||
                (ch >= 'A' && ch <= 'Z') ||
                (ch >= '0' && ch <= '9'))
            {
                builder.Append(ch);
                lastWasSeparator = false;
            }
            else if (ch == '-' || ch == '_' || ch == ' ')
            {
                if (!lastWasSeparator && builder.Length > 0)
                {
                    builder.Append('-');
                    lastWasSeparator = true;
                }
            }
        }

        if (builder.Length == 0)
        {
            return Fallback;
        }

        while (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }

        if (builder.Length == 0)
        {
            return Fallback;
        }

        if (builder.Length > MaxLength)
        {
            builder.Length = MaxLength;
            while (builder.Length > 0 && builder[^1] == '-')
            {
                builder.Length--;
            }
        }

        return builder.Length == 0 ? Fallback : builder.ToString();
    }
}

/// <summary>
/// API request model for migration source inventory scans.
/// </summary>
internal sealed record MigrationInventoryScanRequest
{
    /// <summary>
    /// Source kind identifier.
    /// </summary>
    public string? SourceKind { get; init; }

    /// <summary>
    /// Source URL to scan.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Optional username for source authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password for source authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Optional ArcGIS credential descriptor for GeoServices source scans.
    /// </summary>
    public GeoservicesCredentialDescriptor? Credentials { get; init; }

    /// <summary>
    /// Optional scan timeout in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Whether to include GeoServer style content when available.
    /// </summary>
    public bool? IncludeStyleContent { get; init; }

    /// <summary>
    /// Optional OGC service version to request during discovery.
    /// </summary>
    public string? ServiceVersion { get; init; }

    /// <summary>
    /// Artifact response mode. Use <c>all</c> to include inventory, manifest, and parity evidence.
    /// </summary>
    public string? ArtifactSet { get; init; }

    /// <summary>
    /// Optional target service name used when generating a migration manifest.
    /// </summary>
    public string? TargetServiceName { get; init; }
}

/// <summary>
/// Response envelope for callers that request all migration scan artifacts.
/// </summary>
internal sealed record MigrationScanArtifactSet
{
    /// <summary>
    /// Source inventory artifact.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Generated migration manifest artifact.
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }

    /// <summary>
    /// Generated parity evidence artifact.
    /// </summary>
    public required MigrationParityEvidenceArtifact ParityEvidence { get; init; }
}
