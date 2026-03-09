// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for metadata versioning and GitOps manifest workflows.
/// </summary>
internal static class AdminMetadataEndpoints
{
    private const string DefaultNamespace = "default";

    /// <summary>
    /// Map metadata version and manifest endpoints to the admin API group.
    /// </summary>
    public static void MapAdminMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Metadata")
            .RequireAdminAuthorization();

        _ = group.Map("/version", HandleGetVersion)
            .WithName("GetAdminVersion")
            .WithSummary("Get admin API version info")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/capabilities", HandleGetCapabilities)
            .WithName("GetAdminCapabilities")
            .WithSummary("Get admin API capabilities")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/compatibility", HandleGetCompatibility)
            .WithName("GetServerCompatibility")
            .WithSummary("Get server SDK compatibility metadata")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/manifest", HandleGetManifest)
            .WithName("GetMetadataManifest")
            .WithSummary("Export metadata manifest")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        _ = group.Map("/manifest/apply", HandleApplyManifest)
            .WithName("ApplyMetadataManifest")
            .WithSummary("Apply metadata manifest")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task HandleGetVersion(
        HttpContext context,
        [FromServices] IMetadataSchemaRegistry schemaRegistry)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        var response = new AdminVersionResponse
        {
            Version = version,
            MetadataApiVersion = schemaRegistry.CurrentApiVersion,
            ServerTime = DateTimeOffset.UtcNow
        };

        var payload = ApiResponse<AdminVersionResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseAdminVersionResponse);
    }

    private static async Task HandleGetCapabilities(
        HttpContext context,
        [FromServices] IMetadataSchemaRegistry schemaRegistry)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var versions = new List<string>
        {
            schemaRegistry.CurrentApiVersion,
            schemaRegistry.LegacyApiVersion
        };

        var response = new AdminCapabilitiesResponse
        {
            MetadataApiVersions = versions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ResourceKinds = MetadataResourceKinds.All,
            ManifestSupported = true,
            ManifestDryRunSupported = true,
            ManifestPruneSupported = true
        };

        var payload = ApiResponse<AdminCapabilitiesResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseAdminCapabilitiesResponse);
    }

    // SDK minimum version constants — updated when new SDK releases ship.
    private const string MinSdkVersionJs = "0.1.0";
    private const string MinSdkVersionPython = "0.1.0";
    private const string MinSdkVersionDotnet = "0.1.0";
    private const string CompatibilityContractVersion = "2026.1";
    private const string DefaultEdition = "community";
    private const string DefaultReleaseChannel = "stable";
    private const string DefaultControlPlaneApiVersion = "v1";

    private static async Task HandleGetCompatibility(
        HttpContext context,
        [FromServices] IConfiguration configuration)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        var edition = configuration["Honua:Edition"] ?? DefaultEdition;
        var releaseChannel = configuration["Honua:ReleaseChannel"] ?? DefaultReleaseChannel;

        var capabilities = BuildCapabilities(edition);

        var response = new ServerCompatibilityResponse
        {
            Version = version,
            ControlPlaneApiVersion = DefaultControlPlaneApiVersion,
            ReleaseChannel = releaseChannel,
            Edition = edition.ToLowerInvariant(),
            ServerTime = DateTimeOffset.UtcNow,
            Sdk = new SdkCompatibilityInfo
            {
                MinimumSupportedVersions = new Dictionary<string, string>
                {
                    ["js"] = MinSdkVersionJs,
                    ["python"] = MinSdkVersionPython,
                    ["dotnet"] = MinSdkVersionDotnet
                },
                CompatibilityContract = CompatibilityContractVersion
            },
            Capabilities = capabilities,
            Deprecations = Array.Empty<DeprecationNotice>()
        };

        var payload = ApiResponse<ServerCompatibilityResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseServerCompatibilityResponse);
    }

    private static Dictionary<string, bool> BuildCapabilities(string edition)
    {
        var isProOrHigher = string.Equals(edition, "pro", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(edition, "enterprise", StringComparison.OrdinalIgnoreCase);
        var isEnterprise = string.Equals(edition, "enterprise", StringComparison.OrdinalIgnoreCase);

        return new Dictionary<string, bool>
        {
            ["grpcStreaming"] = isProOrHigher,
            ["distributedCache"] = isProOrHigher,
            ["offlineSync"] = isProOrHigher,
            ["cdc"] = isProOrHigher,
            ["spatialAnalytics"] = isProOrHigher,
            ["aiSpatialAgent"] = isProOrHigher,
            ["sso"] = isEnterprise,
            ["rbac"] = isEnterprise,
            ["multiTenancy"] = isEnterprise,
            ["pluginSdk"] = isEnterprise
        };
    }

    private static async Task HandleGetManifest(
        HttpContext context,
        [FromServices] IMetadataResourceStore store,
        [FromServices] IMetadataSchemaRegistry schemaRegistry)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var @namespace = context.Request.Query["namespace"].ToString();
        var resources = await store.ListAsync(
            null,
            string.IsNullOrWhiteSpace(@namespace) ? null : @namespace,
            context.RequestAborted);

        var drifted = new List<MetadataResourceIdentifier>();
        foreach (var resource in resources)
        {
            var lastApplied = resource.Metadata?.Annotations != null &&
                              resource.Metadata.Annotations.TryGetValue(MetadataAnnotations.LastAppliedManifestHash, out var hash)
                ? hash
                : null;

            if (!string.IsNullOrWhiteSpace(lastApplied))
            {
                var currentHash = ComputeSpecHash(resource.Spec);
                if (!string.Equals(lastApplied, currentHash, StringComparison.Ordinal))
                {
                    drifted.Add(new MetadataResourceIdentifier(
                        resource.Kind ?? string.Empty,
                        resource.Metadata?.Namespace ?? DefaultNamespace,
                        resource.Metadata?.Name ?? string.Empty));
                }
            }
        }

        var manifest = new MetadataManifest
        {
            ApiVersion = schemaRegistry.CurrentApiVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = resources,
            DriftedResources = drifted,
            ManifestHash = ComputeManifestHash(resources)
        };

        var payload = ApiResponse<MetadataManifest>.CreateSuccess(manifest);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseMetadataManifest);
    }

    private static async Task HandleApplyManifest(
        HttpContext context,
        ManifestApplyRequest request,
        [FromServices] IMetadataResourceStore store,
        [FromServices] IMetadataSchemaRegistry schemaRegistry,
        [FromServices] IMetadataCompiler compiler)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var requestResources = request.Resources ?? Array.Empty<MetadataResource>();
        if (requestResources.Count == 0)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "Manifest must include resources.");
            return;
        }

        var entries = new List<ManifestApplyEntry>();
        var created = 0;
        var updated = 0;
        var deleted = 0;
        var skipped = 0;

        var normalizedResources = new List<MetadataResource>();
        foreach (var resource in requestResources)
        {
            var normalized = NormalizeResource(resource, null, null);
            var validation = schemaRegistry.ValidateAndUpgrade(normalized);
            if (!validation.IsValid || validation.Resource == null)
            {
                await WriteError(context, StatusCodes.Status400BadRequest,
                    validation.Errors.Count > 0 ? string.Join(" ", validation.Errors) : "Manifest validation failed.");
                return;
            }

            normalizedResources.Add(validation.Resource);
        }

        var identifiers = normalizedResources
            .Select(resource => new MetadataResourceIdentifier(
                resource.Kind ?? string.Empty,
                resource.Metadata?.Namespace ?? DefaultNamespace,
                resource.Metadata?.Name ?? string.Empty))
            .ToHashSet();

        foreach (var resource in normalizedResources)
        {
            var identifier = new MetadataResourceIdentifier(
                resource.Kind ?? string.Empty,
                resource.Metadata?.Namespace ?? DefaultNamespace,
                resource.Metadata?.Name ?? string.Empty);

            var existing = await store.GetAsync(identifier, context.RequestAborted);
            var specHash = ComputeSpecHash(resource.Spec);
            var updatedMetadata = ApplyManifestHash(resource.Metadata ?? new ResourceMetadata(), specHash);

            if (existing == null)
            {
                if (request.DryRun)
                {
                    created++;
                    entries.Add(new ManifestApplyEntry { Action = "create", Resource = identifier });
                    continue;
                }

                var compilation = await compiler.CompileAsync(resource, context.RequestAborted);
                var resourceWithStatus = new MetadataResource
                {
                    ApiVersion = resource.ApiVersion,
                    Kind = resource.Kind,
                    Metadata = updatedMetadata,
                    Spec = resource.Spec,
                    Status = compilation.Status
                };

                var createResult = await store.CreateAsync(resourceWithStatus, context.RequestAborted);
                if (createResult.Outcome == MetadataResourceWriteOutcome.Conflict)
                {
                    skipped++;
                    entries.Add(new ManifestApplyEntry
                    {
                        Action = "skip",
                        Resource = identifier,
                        Message = "Resource already exists."
                    });
                    continue;
                }

                if (createResult.Resource == null)
                {
                    skipped++;
                    entries.Add(new ManifestApplyEntry
                    {
                        Action = "skip",
                        Resource = identifier,
                        Message = createResult.Error ?? "Failed to create resource."
                    });
                    continue;
                }

                await StoreArtifactAsync(store, compilation, createResult.Resource, context.RequestAborted);
                created++;
                entries.Add(new ManifestApplyEntry { Action = "create", Resource = identifier });
                continue;
            }

            var currentHash = ComputeSpecHash(existing.Spec);
            var lastApplied = existing.Metadata?.Annotations != null &&
                              existing.Metadata.Annotations.TryGetValue(MetadataAnnotations.LastAppliedManifestHash, out var storedHash)
                ? storedHash
                : null;

            if (string.Equals(currentHash, specHash, StringComparison.Ordinal) &&
                string.Equals(lastApplied, specHash, StringComparison.Ordinal))
            {
                skipped++;
                entries.Add(new ManifestApplyEntry { Action = "skip", Resource = identifier, Message = "No changes." });
                continue;
            }

            if (request.DryRun)
            {
                updated++;
                entries.Add(new ManifestApplyEntry { Action = "update", Resource = identifier });
                continue;
            }

            var normalized = NormalizeResource(resource, identifier, existing);
            var merged = new MetadataResource
            {
                ApiVersion = normalized.ApiVersion,
                Kind = normalized.Kind,
                Metadata = updatedMetadata,
                Spec = normalized.Spec,
                Status = normalized.Status
            };

            var compilationResult = await compiler.CompileAsync(merged, context.RequestAborted);
            var resourceWithStatusUpdate = new MetadataResource
            {
                ApiVersion = merged.ApiVersion,
                Kind = merged.Kind,
                Metadata = merged.Metadata,
                Spec = merged.Spec,
                Status = compilationResult.Status
            };

            var expectedVersion = ParseResourceVersion(existing.Metadata?.ResourceVersion);
            var updateResult = await store.UpdateAsync(resourceWithStatusUpdate, expectedVersion, context.RequestAborted);
            if (updateResult.Resource == null)
            {
                skipped++;
                entries.Add(new ManifestApplyEntry
                {
                    Action = "skip",
                    Resource = identifier,
                    Message = updateResult.Error ?? "Failed to update resource."
                });
                continue;
            }

            await StoreArtifactAsync(store, compilationResult, updateResult.Resource, context.RequestAborted);
            updated++;
            entries.Add(new ManifestApplyEntry { Action = "update", Resource = identifier });
        }

        if (request.Prune)
        {
            var namespaces = normalizedResources
                .Select(resource => resource.Metadata?.Namespace ?? DefaultNamespace)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var kinds = normalizedResources
                .Select(resource => resource.Kind ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var ns in namespaces)
            {
                foreach (var kind in kinds)
                {
                    var existingResources = await store.ListAsync(kind, ns, context.RequestAborted);
                    foreach (var candidate in existingResources)
                    {
                        var candidateId = new MetadataResourceIdentifier(
                            candidate.Kind ?? string.Empty,
                            candidate.Metadata?.Namespace ?? DefaultNamespace,
                            candidate.Metadata?.Name ?? string.Empty);
                        if (identifiers.Contains(candidateId))
                        {
                            continue;
                        }

                        if (request.DryRun)
                        {
                            deleted++;
                            entries.Add(new ManifestApplyEntry { Action = "delete", Resource = candidateId });
                            continue;
                        }

                        var expectedVersion = ParseResourceVersion(candidate.Metadata?.ResourceVersion);
                        var deleteResult = await store.DeleteAsync(candidateId, expectedVersion, context.RequestAborted);
                        if (deleteResult.Outcome == MetadataResourceWriteOutcome.Deleted)
                        {
                            deleted++;
                            entries.Add(new ManifestApplyEntry { Action = "delete", Resource = candidateId });
                        }
                        else
                        {
                            skipped++;
                            entries.Add(new ManifestApplyEntry
                            {
                                Action = "skip",
                                Resource = candidateId,
                                Message = "Failed to delete resource."
                            });
                        }
                    }
                }
            }
        }

        var result = new ManifestApplyResult
        {
            DryRun = request.DryRun,
            Summary = new ManifestApplySummary
            {
                Created = created,
                Updated = updated,
                Deleted = deleted,
                Skipped = skipped
            },
            Entries = entries
        };

        var payload = ApiResponse<ManifestApplyResult>.CreateSuccess(result);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseManifestApplyResult);
    }

    private static MetadataResource NormalizeResource(
        MetadataResource resource,
        MetadataResourceIdentifier? identifier,
        MetadataResource? existing)
    {
        var metadata = resource.Metadata ?? new ResourceMetadata();
        var name = identifier?.Name ?? metadata.Name ?? existing?.Metadata?.Name;
        var @namespace = identifier?.Namespace ?? metadata.Namespace ?? existing?.Metadata?.Namespace ?? DefaultNamespace;
        var kind = identifier?.Kind ?? resource.Kind ?? existing?.Kind;

        metadata = metadata with
        {
            Name = name,
            Namespace = @namespace,
            Id = existing?.Metadata?.Id ?? metadata.Id,
            CreatedAt = existing?.Metadata?.CreatedAt ?? metadata.CreatedAt
        };

        return new MetadataResource
        {
            ApiVersion = resource.ApiVersion ?? existing?.ApiVersion,
            Kind = kind,
            Metadata = metadata,
            Spec = resource.Spec,
            Status = resource.Status
        };
    }

    private static ResourceMetadata ApplyManifestHash(ResourceMetadata metadata, string hash)
    {
        var annotations = metadata.Annotations == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata.Annotations, StringComparer.OrdinalIgnoreCase);

        annotations[MetadataAnnotations.LastAppliedManifestHash] = hash;

        return metadata with
        {
            Annotations = annotations
        };
    }

    private static async Task StoreArtifactAsync(
        IMetadataResourceStore store,
        MetadataCompilationResult compilation,
        MetadataResource resource,
        CancellationToken cancellationToken)
    {
        var artifact = new CompiledMetadataArtifact
        {
            ResourceId = resource.Metadata?.Id,
            ApiVersion = resource.ApiVersion,
            Kind = resource.Kind,
            ResourceVersion = resource.Metadata?.ResourceVersion,
            Spec = resource.Spec,
            GeneratedAt = compilation.Artifact.GeneratedAt,
            CompilerVersion = compilation.Artifact.CompilerVersion
        };

        await store.StoreCompiledArtifactAsync(artifact, cancellationToken);
    }

    private static string ComputeSpecHash(JsonElement spec)
    {
        var raw = CanonicalizeJson(spec);
        var bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string ComputeManifestHash(IReadOnlyList<MetadataResource> resources)
    {
        if (resources.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var resource in resources
                     .OrderBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Metadata?.Namespace, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Metadata?.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(resource.Kind).Append('|')
                .Append(resource.Metadata?.Namespace).Append('|')
                .Append(resource.Metadata?.Name).Append('|')
                .Append(resource.Metadata?.ResourceVersion).Append('|')
                .Append(CanonicalizeJson(resource.Spec));
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static long ParseResourceVersion(string? resourceVersion)
    {
        if (string.IsNullOrWhiteSpace(resourceVersion))
        {
            return 0;
        }

        return long.TryParse(resourceVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static Task WriteError(HttpContext context, int statusCode, string message)
        => AdminResponseWriter.WriteErrorAsync(context, message, statusCode);

    private static string CanonicalizeJson(JsonElement element)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCanonicalJson(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
        }
    }

}
