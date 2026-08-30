// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

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
            OperationId = OperationId,
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

    public OperationApprovalReplayMapping MapReplay(OperationGatewayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = JsonSerializer.Deserialize(
                request.Plan?.ExecutionPayload ?? request.ExecutionPayload
                    ?? throw new InvalidOperationException("The persisted service.publish replay payload is unavailable."),
                ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload)
            ?? throw new InvalidOperationException("The persisted service.publish replay payload is invalid.");
        return new OperationApprovalReplayMapping
        {
            Request = payload.ToOperationRequest(),
            TenantId = payload.TenantId,
            SchemaName = payload.SchemaName,
        };
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
