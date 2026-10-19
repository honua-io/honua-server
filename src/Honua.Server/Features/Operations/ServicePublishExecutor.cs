// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Ai.Protocols.Mcp;
using Honua.Server.Features.Admin;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Concrete executor for <c>service.publish</c>. It WRAPS the existing
/// <see cref="ILayerPublishingService"/> rather than reimplementing publish:
/// <see cref="ValidateAsync"/> delegates to <c>ValidateTableForPublishAsync</c> and
/// <see cref="SubmitAsync"/> maps the request onto a <see cref="LayerPublishRequest"/> and
/// calls <c>PublishLayerAsync</c>. The resulting Metadata v2 revision is surfaced on the
/// returned handle.
/// </summary>
internal sealed class ServicePublishExecutor : IOperationExecutor
{
    private readonly ILayerPublishingService _publishingService;
    private readonly ISecureConnectionResolver _connectionResolver;
    private readonly IMetadataV2GraphProvider _metadataGraphProvider;
    private readonly TimeProvider _clock;
    private readonly IMcpNotificationPublisher? _notifications;
    private readonly ILayerPublishJobs? _jobs;
    private readonly IPublishedServiceStore? _publishedServices;

    /// <summary>
    /// Initializes a new instance of <see cref="ServicePublishExecutor"/>.
    /// </summary>
    /// <param name="publishingService">Shared layer publishing service (reused, not reimplemented).</param>
    /// <param name="connectionResolver">Resolves the target connection string from the request connection id.</param>
    /// <param name="metadataGraphProvider">Read access to the Metadata v2 graph for revision surfacing.</param>
    /// <param name="clock">Time provider for handle id generation.</param>
    /// <param name="notifications">Optional catalog-change broadcaster resolved by production composition.</param>
    /// <param name="jobs">
    /// Accepts the publish off the request token. Omitted only by tests that construct the executor
    /// directly; production composition always supplies it.
    /// </param>
    /// <param name="publishedServices">Optional persisted service store for promotion identity checks.</param>
    public ServicePublishExecutor(
        ILayerPublishingService publishingService,
        ISecureConnectionResolver connectionResolver,
        IMetadataV2GraphProvider metadataGraphProvider,
        TimeProvider clock,
        IMcpNotificationPublisher? notifications = null,
        ILayerPublishJobs? jobs = null,
        IPublishedServiceStore? publishedServices = null)
    {
        ArgumentNullException.ThrowIfNull(publishingService);
        ArgumentNullException.ThrowIfNull(connectionResolver);
        ArgumentNullException.ThrowIfNull(metadataGraphProvider);
        ArgumentNullException.ThrowIfNull(clock);
        _publishingService = publishingService;
        _connectionResolver = connectionResolver;
        _metadataGraphProvider = metadataGraphProvider;
        _clock = clock;
        _notifications = notifications;
        _jobs = jobs;
        _publishedServices = publishedServices;
    }

    /// <inheritdoc />
    public string OperationId => ServicePublishOperation.OperationId;

    /// <inheritdoc />
    public async Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connectionString = await ResolveConnectionStringAsync(request.ConnectionId, cancellationToken)
            .ConfigureAwait(false);
        var validationRequest = new TablePublishValidationRequest
        {
            Schema = GetRequired(request, "schema"),
            Table = GetRequired(request, "table"),
            LayerName = GetOptional(request, "layerName"),
            ServiceName = request.ServiceName,
            TargetSrid = GetInt(request, "srid"),
            GeometryColumn = GetOptional(request, "geometryColumn"),
            PrimaryKey = GetOptional(request, "primaryKey"),
            Fields = request.Fields
        };

        var result = await _publishingService
            .ValidateTableForPublishAsync(connectionString, validationRequest, cancellationToken)
            .ConfigureAwait(false);

        return new OperationValidation
        {
            IsValid = result.IsValid,
            Status = result.Status,
            Messages = result.Checks.Select(check => $"{check.Severity}: {check.Message}").ToArray()
        };
    }

    /// <inheritdoc />
    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var connectionString = context.ResolvedConnectionString
            ?? await ResolveConnectionStringAsync(request.ConnectionId, cancellationToken).ConfigureAwait(false);
        var publishRequest = new LayerPublishRequest
        {
            Schema = GetRequired(request, "schema"),
            Table = GetRequired(request, "table"),
            LayerName = GetRequired(request, "layerName"),
            Description = GetOptional(request, "description"),
            GeometryColumn = GetOptional(request, "geometryColumn"),
            GeometryType = GetOptional(request, "geometryType"),
            Srid = GetInt(request, "srid"),
            PrimaryKey = GetOptional(request, "primaryKey"),
            Fields = request.Fields,
            ServiceName = request.ServiceName,
            ConnectionId = Guid.TryParse(request.ConnectionId, out var connectionId) ? connectionId : null
        };

        PublishedLayerSummary summary;
        if (_jobs is not null)
        {
            var routeId = request.ConnectionId!;
            var admission = await _jobs.SubmitAsync(
                connectionString,
                publishRequest.ConnectionId ?? Guid.Empty,
                publishRequest,
                routeId,
                cancellationToken).ConfigureAwait(false);
            if (admission.Kind == LayerPublishAdmissionKind.Unavailable)
            {
                throw LayerPublishRefusals.Unavailable();
            }

            if (admission.Kind == LayerPublishAdmissionKind.Queued)
            {
                var queuedAt = _clock.GetUtcNow();
                return new OperationHandle
                {
                    OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
                    OperationId = OperationId,
                    CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
                    Status = OperationHandleStatus.Queued,
                    CreatedAt = queuedAt,
                    UpdatedAt = queuedAt,
                    JobId = admission.JobId,
                    Result = new OperationResultSummary
                    {
                        Summary = $"Queued layer publish ({admission.RowsTotal.ToString(CultureInfo.InvariantCulture)} rows).",
                        Details = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["jobId"] = admission.JobId!,
                            ["statusUrl"] = admission.StatusUrl!,
                            ["rowsTotal"] = admission.RowsTotal.ToString(CultureInfo.InvariantCulture),
                        }
                    }
                };
            }

            summary = admission.Layer!;
        }
        else
        {
            summary = await _publishingService
                .PublishLayerAsync(connectionString, publishRequest, cancellationToken)
                .ConfigureAwait(false);
        }

        // The publisher returns the revision of its own committed graph mutation.
        // A later current-graph read could attribute another publisher's revision to this job.
        if (_notifications is { } notifications)
        {
            TryNotify(() => { notifications.BroadcastResourcesListChanged(); });
            TryNotify(() => { notifications.BroadcastToolsListChanged(); });
        }
        var now = _clock.GetUtcNow();
        var promotionReadback = await ResolvePromotionReadbackAsync(summary.ServiceName, CancellationToken.None)
            .ConfigureAwait(false);
        var resultDetails = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["layerId"] = summary.LayerId.ToString(CultureInfo.InvariantCulture),
            ["layerName"] = summary.LayerName,
            ["geometryType"] = summary.GeometryType,
        };
        if (promotionReadback != PromotionReadback.Available)
        {
            resultDetails["readbackStatus"] = "Unavailable";
            resultDetails["readbackReason"] = promotionReadback == PromotionReadback.Collision
                ? "The published service identity conflicts with a promotion record."
                : "The published service identity could not be checked after publication committed.";
        }
        else
        {
            resultDetails["serviceName"] = summary.ServiceName;
        }
        if (summary.MetadataRevision is > 0)
        {
            resultDetails["metadataRevision"] = summary.MetadataRevision.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            MetadataRevision = summary.MetadataRevision,
            ResourceIds = promotionReadback != PromotionReadback.Available
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["layerId"] = summary.LayerId.ToString(CultureInfo.InvariantCulture),
                }
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["layerId"] = summary.LayerId.ToString(CultureInfo.InvariantCulture),
                    ["serviceName"] = summary.ServiceName,
                },
            Result = new OperationResultSummary
            {
                Summary = $"Published layer '{summary.LayerName}' (id {summary.LayerId}) to service '{summary.ServiceName}'.",
                Details = resultDetails
            }
        };
    }

    /// <inheritdoc />
    public async Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (_jobs is null || string.IsNullOrWhiteSpace(handle.JobId))
        {
            return Echo(handle);
        }

        var progress = await _jobs.GetAsync(handle.JobId, cancellationToken).ConfigureAwait(false);
        if (progress is null)
        {
            return Echo(handle);
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jobId"] = progress.OperationId,
            ["rowsMaterialized"] = progress.RowsMaterialized.ToString(CultureInfo.InvariantCulture),
            ["rowsTotal"] = progress.RowsTotal.ToString(CultureInfo.InvariantCulture),
            ["status"] = progress.Status.ToString(),
        };
        var resourceIds = new Dictionary<string, string>(handle.ResourceIds, StringComparer.Ordinal);
        // A prior poll may have advertised this identity. Re-resolve it from
        // the current persisted stores on every poll before carrying it forward.
        resourceIds.Remove("serviceName");
        resourceIds.Remove("serviceUri");
        var metadataRevision = progress.MetadataRevision ?? handle.MetadataRevision;
        if (progress.LayerId is int layerId)
        {
            var layerText = layerId.ToString(CultureInfo.InvariantCulture);
            details["layerId"] = layerText;
            resourceIds["layerId"] = layerText;
        }

        if (progress.Status == Honua.Core.Features.Infrastructure.Domain.OperationStatus.Completed &&
            progress.LayerId is int committedLayerId)
        {
            var resolved = false;
            var promotionReadback = PromotionReadback.Available;
            var serviceName = string.Empty;
            string? serviceUri = null;
            try
            {
                var graph = await _metadataGraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                resolved = TryResolvePublishedService(graph, committedLayerId, progress.TenantId, out serviceName);
                if (resolved)
                {
                    promotionReadback = await ResolvePromotionReadbackAsync(serviceName, cancellationToken)
                        .ConfigureAwait(false);
                    if (promotionReadback == PromotionReadback.Available)
                    {
                        serviceUri = McpResourceUris.PublishedServiceUri(serviceName);
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The worker has committed. Readback enrichment cannot change
                // completion into a retryable failure or invent a service URI.
                promotionReadback = PromotionReadback.Unavailable;
            }

            if (resolved && serviceUri is not null && promotionReadback == PromotionReadback.Available)
            {
                details["serviceName"] = serviceName;
                details["serviceUri"] = serviceUri;
                if (metadataRevision is > 0)
                {
                    details["metadataRevision"] = metadataRevision.Value.ToString(CultureInfo.InvariantCulture);
                }
                details["readbackStatus"] = "Resolved";
                resourceIds["serviceName"] = serviceName;
            }
            else
            {
                // The publication committed, but a missing or ambiguous catalog
                // identity cannot truthfully produce a service URI. Never retry it.
                details["readbackStatus"] = "Unavailable";
                details["readbackReason"] = promotionReadback switch
                {
                    PromotionReadback.Collision => "The published service identity conflicts with a promotion record.",
                    PromotionReadback.Unavailable => "The published service identity could not be checked after publication committed.",
                    _ => "The committed layer has no unique persisted feature-service identity.",
                };
            }
        }

        return Echo(handle) with
        {
            Status = MapStatus(progress.Status),
            MetadataRevision = metadataRevision,
            UpdatedAt = progress.CompletedAt ?? handle.UpdatedAt,
            Reason = progress.ErrorMessage ?? handle.Reason,
            ResourceIds = resourceIds,
            Result = new OperationResultSummary
            {
                Summary = progress.Status == Honua.Core.Features.Infrastructure.Domain.OperationStatus.Completed &&
                    progress.LayerId is int published
                    ? $"Published layer id {published.ToString(CultureInfo.InvariantCulture)}."
                    : $"Layer publish {progress.Status}.",
                Details = details,
            },
        };
    }

    private enum PromotionReadback
    {
        Available,
        Collision,
        Unavailable,
    }

    private static void TryNotify(Action notify)
    {
        try
        {
            notify();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Notification fanout is best effort after the catalog commit.
            // A listener outage cannot make a non-idempotent publish retryable.
        }
    }

    private async Task<PromotionReadback> ResolvePromotionReadbackAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        if (_publishedServices is null)
        {
            return PromotionReadback.Available;
        }

        try
        {
            return await _publishedServices.FindByPublicIdAsync(serviceName, cancellationToken)
                .ConfigureAwait(false) is null
                ? PromotionReadback.Available
                : PromotionReadback.Collision;
        }
        catch (PublishedServiceIdentityAmbiguousException)
        {
            // The layer is already committed. Ambiguous promotions make its URI
            // unreadable, but must not turn completion into a retryable failure.
            return PromotionReadback.Collision;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A store outage after actuation cannot make this non-idempotent
            // publish safe to retry. Withhold the readback URI instead.
            return PromotionReadback.Unavailable;
        }
    }

    private static bool TryResolvePublishedService(
        MetadataV2GraphSnapshot? snapshot,
        int storageLayerId,
        string? tenantId,
        out string serviceName)
    {
        serviceName = string.Empty;
        if (snapshot is null)
        {
            return false;
        }

        // Durable completion records the storage-layer identity. A public layer
        // index can differ, and the submitted service name may have been an alias.
        // Reconstruct only from the committed graph, including after a restart.
        var publications = snapshot.Graph.Publications
            .Where(publication =>
                publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
                publication.LayerIndex is >= 0 &&
                snapshot.ResolveResource(publication) is not null &&
                snapshot.ResolveStorageBinding(publication) is { } binding &&
                binding.ResourceId == publication.ResourceId &&
                binding.StorageLayerId is >= 0 &&
                (string.IsNullOrEmpty(publication.StorageBindingId) ||
                    publication.StorageBindingId == binding.Metadata.Id) &&
                snapshot.Index.ServicesById.ContainsKey(publication.ServiceId))
            .Where(publication => MetadataV2TenantVisibility.IsVisibleToTenant(
                publication, snapshot.ResolveResource(publication),
                snapshot.Index.ServicesById[publication.ServiceId], tenantId))
            .ToArray();
        var services = publications
            .Where(publication => snapshot.ResolveStorageBinding(publication)!.StorageLayerId == storageLayerId)
            .Select(publication => snapshot.Index.ServicesById[publication.ServiceId])
            .DistinctBy(service => service.Metadata.Id, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (services.Length != 1 || string.IsNullOrWhiteSpace(services[0].Metadata.Name))
        {
            return false;
        }

        // A resource URI uses the public name, so a different storage layer in
        // another service with that same name is still an ambiguous readback.
        var selected = services[0];
        if (publications.Any(publication =>
                publication.ServiceId != selected.Metadata.Id &&
                string.Equals(snapshot.Index.ServicesById[publication.ServiceId].Metadata.Name,
                    selected.Metadata.Name, StringComparison.OrdinalIgnoreCase)) ||
            publications.Where(publication => publication.ServiceId == selected.Metadata.Id)
                .GroupBy(publication => publication.LayerIndex).Any(group => group.Count() > 1))
        {
            return false;
        }

        // Publishing into a stopped service can complete successfully. Returning
        // its identity does not reactivate it; the resource reports its lifecycle.
        serviceName = services[0].Metadata.Name;
        return true;
    }

    private static OperationStatus Echo(OperationHandle handle)
        => new()
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            AuditId = handle.AuditId,
            ProposalId = handle.ProposalId,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            AuthorizationOutcome = handle.AuthorizationOutcome,
            PolicyDecision = handle.PolicyDecision,
            Status = handle.Status,
            Result = handle.Result,
            JobId = handle.JobId,
            MetadataRevision = handle.MetadataRevision,
            ApprovalLane = handle.ApprovalLane,
            Reason = handle.Reason,
            ResourceIds = handle.ResourceIds,
            EvidenceRefs = handle.EvidenceRefs,
        };

    private static OperationHandleStatus MapStatus(Honua.Core.Features.Infrastructure.Domain.OperationStatus status)
        => status switch
        {
            Honua.Core.Features.Infrastructure.Domain.OperationStatus.Queued => OperationHandleStatus.Queued,
            Honua.Core.Features.Infrastructure.Domain.OperationStatus.Processing => OperationHandleStatus.Running,
            Honua.Core.Features.Infrastructure.Domain.OperationStatus.Completed => OperationHandleStatus.Completed,
            Honua.Core.Features.Infrastructure.Domain.OperationStatus.Failed => OperationHandleStatus.Failed,
            Honua.Core.Features.Infrastructure.Domain.OperationStatus.Cancelled => OperationHandleStatus.Cancelled,
            _ => OperationHandleStatus.Running,
        };

    private async Task<string> ResolveConnectionStringAsync(string? connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("service.publish requires a 'connectionId' on the operation request.");
        }

        return Guid.TryParse(connectionId, out var id)
            ? await _connectionResolver.ResolveConnectionStringAsync(id, cancellationToken).ConfigureAwait(false)
            : await _connectionResolver.ResolveConnectionStringAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    private static string GetRequired(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value!
            : throw new ArgumentException($"Required operation parameter '{name}' is missing.");

    private static string? GetOptional(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static int? GetInt(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

}
