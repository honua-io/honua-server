// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
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
    public ServicePublishExecutor(
        ILayerPublishingService publishingService,
        ISecureConnectionResolver connectionResolver,
        IMetadataV2GraphProvider metadataGraphProvider,
        TimeProvider clock,
        IMcpNotificationPublisher? notifications = null,
        ILayerPublishJobs? jobs = null)
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
                LayerPublishRequestMapper.ToHttp(publishRequest),
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

        // Surface the resulting Metadata v2 revision in the handle. PublishedLayerSummary does
        // not carry it today (the publish path computes graph.Revision+1 internally in
        // PostgreSqlLayerPublishingService.MetadataV2Graph.cs and persists it without echoing it
        // on the summary), so we read the post-publish current revision from the graph provider.
        // The canonical source is the graph snapshot the publish just saved; surfacing it here is
        // the cheapest read-after-write that keeps the executor a thin wrapper over the service.
        // None, not the request token: an accepted publish must finish after the client disconnects.
        var graph = await _metadataGraphProvider.GetCurrentAsync(
            _jobs is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
        _notifications?.BroadcastResourcesListChanged();
        _notifications?.BroadcastToolsListChanged();
        var now = _clock.GetUtcNow();

        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Completed,
            CreatedAt = now,
            UpdatedAt = now,
            MetadataRevision = graph.Revision,
            ResourceIds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["layerId"] = summary.LayerId.ToString(CultureInfo.InvariantCulture),
                ["serviceName"] = summary.ServiceName,
            },
            Result = new OperationResultSummary
            {
                Summary = $"Published layer '{summary.LayerName}' (id {summary.LayerId}) to service '{summary.ServiceName}'.",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["layerId"] = summary.LayerId.ToString(CultureInfo.InvariantCulture),
                    ["layerName"] = summary.LayerName,
                    ["serviceName"] = summary.ServiceName,
                    ["geometryType"] = summary.GeometryType,
                    ["metadataRevision"] = graph.Revision.ToString(CultureInfo.InvariantCulture)
                }
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
        if (progress.LayerId is int layerId)
        {
            var layerText = layerId.ToString(CultureInfo.InvariantCulture);
            details["layerId"] = layerText;
            resourceIds["layerId"] = layerText;
        }

        return Echo(handle) with
        {
            Status = MapStatus(progress.Status),
            UpdatedAt = progress.CompletedAt ?? handle.UpdatedAt,
            Reason = progress.ErrorMessage ?? handle.Reason,
            ResourceIds = resourceIds,
            Result = new OperationResultSummary
            {
                Summary = progress.LayerId is int published
                    ? $"Published layer id {published.ToString(CultureInfo.InvariantCulture)}."
                    : $"Layer publish {progress.Status}.",
                Details = details,
            },
        };
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
