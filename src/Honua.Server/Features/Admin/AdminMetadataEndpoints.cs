// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
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
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for metadata versioning and GitOps manifest workflows.
/// </summary>
internal static class AdminMetadataEndpoints
{

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

        var version = GetServerVersion();
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
        [FromServices] IMetadataSchemaRegistry schemaRegistry,
        [FromServices] IOptions<ManifestApprovalOptions> approvalOptions,
        [FromServices] IOptions<GitOpsWatchOptions> gitOpsWatchOptions)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await AdminResponseWriter.WriteMethodNotAllowedAsync(context);
            return;
        }

        var compatibility = CreateCompatibilityMetadata(schemaRegistry, approvalOptions.Value.Enabled, gitOpsWatchOptions.Value.Enabled);

        var response = new AdminCapabilitiesResponse
        {
            MetadataApiVersions = compatibility.MetadataSchemas
                .Select(static schema => schema.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ResourceKinds = MetadataResourceKinds.All,
            ManifestSupported = true,
            ManifestDryRunSupported = true,
            ManifestPruneSupported = true,
            Compatibility = compatibility
        };

        var payload = ApiResponse<AdminCapabilitiesResponse>.CreateSuccess(response);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseAdminCapabilitiesResponse);
    }

    private static AdminCompatibilityMetadata CreateCompatibilityMetadata(IMetadataSchemaRegistry schemaRegistry, bool approvalEnabled = false, bool gitOpsWatchEnabled = false)
    {
        var serverVersion = GetServerVersion();
        var schemas = new List<AdminMetadataSchemaCompatibility>
        {
            new()
            {
                Version = schemaRegistry.CurrentApiVersion,
                Deprecated = false
            }
        };

        if (!string.Equals(schemaRegistry.LegacyApiVersion, schemaRegistry.CurrentApiVersion, StringComparison.OrdinalIgnoreCase))
        {
            schemas.Add(new AdminMetadataSchemaCompatibility
            {
                Version = schemaRegistry.LegacyApiVersion,
                Deprecated = true
            });
        }

        return new AdminCompatibilityMetadata
        {
            ServerVersion = serverVersion,
            ReleaseChannel = InferReleaseChannel(serverVersion),
            ControlPlaneApi = new AdminControlPlaneApiCompatibility
            {
                Major = 1,
                BasePath = "/api/v1/admin",
                Deprecated = false
            },
            MetadataSchemas = schemas,
            Features = new AdminCompatibilityFeatureFlags
            {
                MetadataResources = true,
                ManifestExport = true,
                ManifestApply = true,
                ManifestDryRun = true,
                ManifestPrune = true,
                ManifestApproval = approvalEnabled,
                GitOpsWatch = gitOpsWatchEnabled,
                AdminRealtime = true,
                ObservabilityStatus = true
            }
        };
    }

    private static string GetServerVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return SanitizeServerVersion(informationalVersion);
        }

        return SanitizeServerVersion(assembly.GetName().Version?.ToString() ?? "unknown");
    }

    internal static string SanitizeServerVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        var trimmed = version.Trim();
        var metadataSeparatorIndex = trimmed.IndexOf('+');
        return metadataSeparatorIndex >= 0
            ? trimmed[..metadataSeparatorIndex]
            : trimmed;
    }

    private static string InferReleaseChannel(string serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return "stable";
        }

        var normalizedVersion = serverVersion.ToLowerInvariant();
        if (normalizedVersion.Contains("nightly", StringComparison.Ordinal))
        {
            return "nightly";
        }

        if (normalizedVersion.Contains("preview", StringComparison.Ordinal))
        {
            return "preview";
        }

        if (normalizedVersion.Contains("lts", StringComparison.Ordinal))
        {
            return "lts";
        }

        if (normalizedVersion.Contains("alpha", StringComparison.Ordinal))
        {
            return "alpha";
        }

        if (normalizedVersion.Contains("beta", StringComparison.Ordinal))
        {
            return "beta";
        }

        if (normalizedVersion.Contains("-rc", StringComparison.Ordinal) ||
            normalizedVersion.Contains(".rc", StringComparison.Ordinal))
        {
            return "rc";
        }

        if (normalizedVersion.Contains("dev", StringComparison.Ordinal) ||
            normalizedVersion.Contains("ci", StringComparison.Ordinal))
        {
            return "dev";
        }

        return "stable";
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
                var currentHash = ManifestHashHelper.ComputeSpecHash(resource.Spec);
                if (!string.Equals(lastApplied, currentHash, StringComparison.Ordinal))
                {
                    drifted.Add(new MetadataResourceIdentifier(
                        resource.Kind ?? string.Empty,
                        resource.Metadata?.Namespace ?? ManifestHashHelper.DefaultNamespace,
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
        [FromServices] IMetadataCompiler compiler,
        [FromServices] ManifestApprovalGate approvalGate,
        [FromServices] IManifestVersionStore versionStore)
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

        if (request.ApprovalRequired)
        {
            if (!approvalGate.Enabled)
            {
                await WriteError(context, StatusCodes.Status403Forbidden,
                    "Manifest approval workflows require the enterprise edition.");
                return;
            }

            await HandleQueueForApproval(context, request, requestResources, schemaRegistry, approvalGate);
            return;
        }

        var (normalizedResources, validationError) = ValidateAndNormalizeResources(requestResources, schemaRegistry);
        if (normalizedResources == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, validationError!);
            return;
        }

        var result = await ApplyNormalizedResourcesAsync(
            normalizedResources,
            request.DryRun,
            request.Prune,
            store,
            compiler,
            context.RequestAborted,
            versionStore,
            context.User.Identity?.Name);

        var payload = ApiResponse<ManifestApplyResult>.CreateSuccess(result);
        await AdminResponseWriter.WriteJsonAsync(context, payload, MetadataResourceJsonContext.Default.ApiResponseManifestApplyResult);
    }

    /// <summary>
    /// Normalizes and validates a list of resources, returning the validated set or the first error.
    /// Used by both the direct apply and queue-for-approval paths.
    /// </summary>
    internal static (IReadOnlyList<MetadataResource>? Resources, string? Error) ValidateAndNormalizeResources(
        IReadOnlyList<MetadataResource> resources,
        IMetadataSchemaRegistry schemaRegistry)
    {
        var normalizedResources = new List<MetadataResource>();
        foreach (var resource in resources)
        {
            var normalized = NormalizeResource(resource, null, null);
            var validation = schemaRegistry.ValidateAndUpgrade(normalized);
            if (!validation.IsValid || validation.Resource == null)
            {
                var error = validation.Errors.Count > 0 ? string.Join(" ", validation.Errors) : "Manifest validation failed.";
                return (null, error);
            }

            normalizedResources.Add(validation.Resource);
        }

        return (normalizedResources, null);
    }

    /// <summary>
    /// Shared apply engine for pre-validated, normalized resources.
    /// Used by both the direct apply and the approval-based apply flows.
    /// </summary>
    internal static async Task<ManifestApplyResult> ApplyNormalizedResourcesAsync(
        IReadOnlyList<MetadataResource> normalizedResources,
        bool dryRun,
        bool prune,
        IMetadataResourceStore store,
        IMetadataCompiler compiler,
        CancellationToken cancellationToken,
        IManifestVersionStore? versionStore = null,
        string? actor = null)
    {
        var entries = new List<ManifestApplyEntry>();
        var created = 0;
        var updated = 0;
        var deleted = 0;
        var skipped = 0;

        var identifiers = normalizedResources
            .Select(resource => new MetadataResourceIdentifier(
                resource.Kind ?? string.Empty,
                resource.Metadata?.Namespace ?? ManifestHashHelper.DefaultNamespace,
                resource.Metadata?.Name ?? string.Empty))
            .ToHashSet();

        foreach (var resource in normalizedResources)
        {
            var identifier = new MetadataResourceIdentifier(
                resource.Kind ?? string.Empty,
                resource.Metadata?.Namespace ?? ManifestHashHelper.DefaultNamespace,
                resource.Metadata?.Name ?? string.Empty);

            var existing = await store.GetAsync(identifier, cancellationToken);
            var specHash = ManifestHashHelper.ComputeSpecHash(resource.Spec);
            var updatedMetadata = ApplyManifestHash(resource.Metadata ?? new ResourceMetadata(), specHash);

            if (existing == null)
            {
                if (dryRun)
                {
                    created++;
                    entries.Add(new ManifestApplyEntry { Action = "create", Resource = identifier });
                    continue;
                }

                var compilation = await compiler.CompileAsync(resource, cancellationToken);
                var resourceWithStatus = new MetadataResource
                {
                    ApiVersion = resource.ApiVersion,
                    Kind = resource.Kind,
                    Metadata = updatedMetadata,
                    Spec = resource.Spec,
                    Status = compilation.Status
                };

                var createResult = await store.CreateAsync(resourceWithStatus, cancellationToken);
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

                await StoreArtifactAsync(store, compilation, createResult.Resource, cancellationToken);
                created++;
                entries.Add(new ManifestApplyEntry { Action = "create", Resource = identifier });
                continue;
            }

            var currentHash = ManifestHashHelper.ComputeSpecHash(existing.Spec);
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

            if (dryRun)
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

            var compilationResult = await compiler.CompileAsync(merged, cancellationToken);
            var resourceWithStatusUpdate = new MetadataResource
            {
                ApiVersion = merged.ApiVersion,
                Kind = merged.Kind,
                Metadata = merged.Metadata,
                Spec = merged.Spec,
                Status = compilationResult.Status
            };

            var expectedVersion = ParseResourceVersion(existing.Metadata?.ResourceVersion);
            var updateResult = await store.UpdateAsync(resourceWithStatusUpdate, expectedVersion, cancellationToken);
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

            await StoreArtifactAsync(store, compilationResult, updateResult.Resource, cancellationToken);
            updated++;
            entries.Add(new ManifestApplyEntry { Action = "update", Resource = identifier });
        }

        if (prune)
        {
            var namespaces = normalizedResources
                .Select(resource => resource.Metadata?.Namespace ?? ManifestHashHelper.DefaultNamespace)
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
                    var existingResources = await store.ListAsync(kind, ns, cancellationToken);
                    foreach (var candidate in existingResources)
                    {
                        var candidateId = new MetadataResourceIdentifier(
                            candidate.Kind ?? string.Empty,
                            candidate.Metadata?.Namespace ?? ManifestHashHelper.DefaultNamespace,
                            candidate.Metadata?.Name ?? string.Empty);
                        if (identifiers.Contains(candidateId))
                        {
                            continue;
                        }

                        if (dryRun)
                        {
                            deleted++;
                            entries.Add(new ManifestApplyEntry { Action = "delete", Resource = candidateId });
                            continue;
                        }

                        var expectedVersion = ParseResourceVersion(candidate.Metadata?.ResourceVersion);
                        var deleteResult = await store.DeleteAsync(candidateId, expectedVersion, cancellationToken);
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

        if (!dryRun && versionStore != null)
        {
            var manifestResourcesJson = JsonSerializer.SerializeToElement(
                normalizedResources.ToArray(),
                MetadataResourceJsonContext.Default.MetadataResourceArray);
            var manifestHash = ComputeManifestHash(normalizedResources);

            var versionEntry = new ManifestVersionEntry
            {
                VersionId = Guid.NewGuid().ToString("N"),
                ManifestHash = manifestHash,
                ManifestJson = manifestResourcesJson,
                Summary = $"Created: {created}, Updated: {updated}, Deleted: {deleted}, Skipped: {skipped}",
                Actor = actor,
                AppliedAt = DateTimeOffset.UtcNow,
                ResourceCount = normalizedResources.Count
            };

            await versionStore.StoreAsync(versionEntry, cancellationToken);
        }

        return new ManifestApplyResult
        {
            DryRun = dryRun,
            Summary = new ManifestApplySummary
            {
                Created = created,
                Updated = updated,
                Deleted = deleted,
                Skipped = skipped
            },
            Entries = entries
        };
    }

    private static async Task HandleQueueForApproval(
        HttpContext context,
        ManifestApplyRequest request,
        IReadOnlyList<MetadataResource> resources,
        IMetadataSchemaRegistry schemaRegistry,
        ManifestApprovalGate approvalGate)
    {
        var (normalizedResources, validationError) = ValidateAndNormalizeResources(resources, schemaRegistry);
        if (normalizedResources == null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, validationError!);
            return;
        }

        var snapshotJson = JsonSerializer.SerializeToElement(new ManifestApplyRequest
        {
            Resources = normalizedResources,
            DryRun = request.DryRun,
            Prune = request.Prune
        }, MetadataResourceJsonContext.Default.ManifestApplyRequest);

        var manifestHash = ComputeManifestHash(normalizedResources);
        var now = DateTimeOffset.UtcNow;
        var options = approvalGate.Options;
        var expiresAt = options.DefaultTimeoutMinutes.HasValue
            ? now.AddMinutes(options.DefaultTimeoutMinutes.Value)
            : (DateTimeOffset?)null;

        var pending = new ManifestPendingChange
        {
            PendingId = Guid.NewGuid(),
            ManifestSnapshot = snapshotJson,
            ManifestHash = manifestHash,
            Status = ManifestApprovalStatus.Pending,
            RequestedBy = request.RequestedBy,
            RequestedReason = request.RequestedReason,
            DryRun = request.DryRun,
            Prune = request.Prune,
            ResourceCount = normalizedResources.Count,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        await approvalGate.PendingStore.CreateAsync(pending, context.RequestAborted);

        approvalGate.EnqueueWebhook(new ManifestApprovalWebhookEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "manifest-approval-requested",
            PendingId = pending.PendingId,
            ManifestHash = pending.ManifestHash,
            Status = "pending",
            Actor = request.RequestedBy,
            Reason = request.RequestedReason,
            ResourceCount = pending.ResourceCount,
            Timestamp = now
        });

        var response = MapToResponse(pending);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        var payload = ApiResponse<ManifestPendingChangeResponse>.CreateSuccess(response, "Manifest change queued for approval.");
        await AdminResponseWriter.WriteJsonAsync(context, payload, ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);
    }

    internal static ManifestPendingChangeResponse MapToResponse(ManifestPendingChange change) => new()
    {
        PendingId = change.PendingId,
        ManifestHash = change.ManifestHash,
        Status = MapApprovalStatusForResponse(change.Status),
        RequestedBy = change.RequestedBy,
        RequestedReason = change.RequestedReason,
        DecisionBy = change.DecisionBy,
        DecisionReason = change.DecisionReason,
        ResourceCount = change.ResourceCount,
        DryRun = change.DryRun,
        Prune = change.Prune,
        CreatedAt = change.CreatedAt,
        DecidedAt = change.DecidedAt,
        ExpiresAt = change.ExpiresAt
    };

    private static string MapApprovalStatusForResponse(ManifestApprovalStatus status)
        => status == ManifestApprovalStatus.Applying
            ? "pending"
            : status.ToString().ToLowerInvariant();

    internal static MetadataResource NormalizeResource(
        MetadataResource resource,
        MetadataResourceIdentifier? identifier,
        MetadataResource? existing)
    {
        var metadata = resource.Metadata ?? new ResourceMetadata();
        var name = identifier?.Name ?? metadata.Name ?? existing?.Metadata?.Name;
        var @namespace = identifier?.Namespace ?? metadata.Namespace ?? existing?.Metadata?.Namespace ?? ManifestHashHelper.DefaultNamespace;
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

    internal static ResourceMetadata ApplyManifestHash(ResourceMetadata metadata, string hash)
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

    internal static async Task StoreArtifactAsync(
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
    internal static string ComputeManifestHash(IReadOnlyList<MetadataResource> resources)
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
                .Append(ManifestHashHelper.CanonicalizeJson(resource.Spec));
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static long ParseResourceVersion(string? resourceVersion)
    {
        if (string.IsNullOrWhiteSpace(resourceVersion))
        {
            return 0;
        }

        return long.TryParse(resourceVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static Task WriteError(HttpContext context, int statusCode, string message)
        => AdminResponseWriter.WriteErrorAsync(context, message, statusCode);

}
