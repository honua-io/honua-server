// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Maps <c>service.publish</c> approval requests to the publish actuator's durable,
/// typed replay payload. The operation has its own class and is never aliased to an
/// unrelated legacy actuator.
/// </summary>
internal sealed class ServicePublishApprovalRequestMapper : IOperationApprovalRequestMapper
{
    public string OperationId => ServicePublishOperation.OperationId;

    public OperationGatewayRequest Map(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);

        if (!string.Equals(descriptor.OperationId, OperationId, StringComparison.Ordinal) ||
            !string.Equals(request.OperationId, OperationId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The mapper only accepts service.publish requests.", nameof(request));
        }

        var payload = ServicePublishApprovalPayload.From(request, context);
        var serialized = JsonSerializer.Serialize(
            payload,
            ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload);

        return new OperationGatewayRequest
        {
            OperationInstanceId = context.OperationInstanceId,
            Kind = OperationClass.ServicePublish,
            RequestedBy = context.PrincipalId,
            Reason = decision.Reason,
            CorrelationId = context.CorrelationId,
            ExecutionPayload = serialized,
            Plan = new OperationProposalPlan
            {
                Summary = $"Publish '{payload.Schema}.{payload.Table}' as layer '{payload.LayerName}'.",
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = serialized,
            },
        };
    }
}

/// <summary>
/// Approval-replay compatibility actuator for Slice 1. It consumes the exact typed
/// payload produced by <see cref="ServicePublishApprovalRequestMapper"/> and delegates
/// to the same <see cref="ServicePublishExecutor"/> used by direct typed invocation.
/// </summary>
internal sealed class ServicePublishApprovalExecutor(IServiceScopeFactory scopeFactory)
    : Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor
{
    public OperationClass OperationClass => OperationClass.ServicePublish;

    public Task<OperationProposalPlan?> PlanAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(request.Plan);

    public async Task<string?> ExecuteAsync(
        OperationGatewayRequest request,
        string? executionPayload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationInstanceId) ||
            string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            throw new InvalidOperationException("The canonical operation identity is required for approval replay.");
        }

        var payload = JsonSerializer.Deserialize(
                executionPayload ?? throw new InvalidOperationException("The service.publish replay payload is unavailable."),
                ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload)
            ?? throw new InvalidOperationException("The service.publish replay payload is invalid.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetService<RequestTenantContext>();
        var previousTenant = tenantContext?.TenantId;
        var previousTenantSource = tenantContext?.Source ?? TenantContextSource.Anonymous;
        tenantContext?.Set(payload.TenantId, TenantContextSource.Claim);
        var schemaContext = scope.ServiceProvider.GetService<SchemaContext>();
        var previousSchema = schemaContext?.CurrentSchema;
        if (schemaContext is not null)
        {
            schemaContext.CurrentSchema = payload.SchemaName;
        }

        var executor = scope.ServiceProvider.GetRequiredService<ServicePublishExecutor>();
        try
        {
            if (payload.DryRun)
            {
                var validation = await executor.ValidateAsync(payload.ToOperationRequest(), cancellationToken)
                    .ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException("The approved dry-run payload no longer validates.");
                }

                return request.OperationInstanceId;
            }

            var result = await executor.SubmitAsync(
                    payload.ToOperationRequest(),
                    new OperationPolicyContext
                    {
                        OperationInstanceId = request.OperationInstanceId,
                        CorrelationId = request.CorrelationId,
                        PrincipalId = request.RequestedBy,
                        TenantId = payload.TenantId,
                        SchemaName = payload.SchemaName,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var notifications = scope.ServiceProvider.GetService<IMcpNotificationPublisher>();
            notifications?.BroadcastResourcesListChanged();
            notifications?.BroadcastToolsListChanged();
            return result.OperationInstanceId;
        }
        finally
        {
            if (schemaContext is not null)
            {
                schemaContext.CurrentSchema = previousSchema;
            }

            tenantContext?.Set(previousTenant, previousTenantSource);
        }
    }
}

internal sealed record ServicePublishApprovalPayload
{
    public required string ConnectionId { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required string LayerName { get; init; }

    public string? ServiceName { get; init; }

    public Dictionary<string, string?> Parameters { get; init; } = new(StringComparer.Ordinal);

    public string[] Fields { get; init; } = [];

    public bool DryRun { get; init; }

    public string? TenantId { get; init; }

    public string? SchemaName { get; init; }

    public static ServicePublishApprovalPayload From(OperationRequest request, OperationPolicyContext context)
        => new()
        {
            ConnectionId = Require(request.ConnectionId, "connectionId"),
            Schema = GetRequiredParameter(request, "schema"),
            Table = GetRequiredParameter(request, "table"),
            LayerName = GetRequiredParameter(request, "layerName"),
            ServiceName = request.ServiceName,
            Parameters = new Dictionary<string, string?>(request.Parameters, StringComparer.Ordinal),
            Fields = request.Fields.ToArray(),
            DryRun = request.DryRun,
            TenantId = context.TenantId,
            SchemaName = context.SchemaName,
        };

    public OperationRequest ToOperationRequest() => new()
    {
        OperationId = ServicePublishOperation.OperationId,
        ConnectionId = ConnectionId,
        ServiceName = ServiceName,
        Parameters = Parameters,
        Fields = Fields,
        DryRun = DryRun,
    };

    private static string GetRequiredParameter(OperationRequest request, string name)
        => request.Parameters.TryGetValue(name, out var value)
            ? Require(value, name)
            : throw new ArgumentException($"Required operation parameter '{name}' is missing.", nameof(request));

    private static string Require(string? value, string name)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required operation parameter '{name}' is missing.", nameof(value));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServicePublishApprovalPayload))]
internal sealed partial class ServicePublishApprovalJsonContext : JsonSerializerContext
{
}
