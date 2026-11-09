// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed record CoordinatedReleaseApprovalBinding(
    string WorkflowId,
    WorkflowOperationKind Kind,
    CoordinatedReleaseStep Gate,
    string? TenantId,
    string? RequesterTenantId,
    string? RequestedBy,
    string? RequesterSubject,
    string? RequesterName,
    string? RequesterTenantClaimType,
    string? RequesterAuthenticationType,
    IReadOnlyList<string> RequesterRoles,
    string PackageId,
    string TargetEnvironment,
    CoordinatedReleaseContainerSpec Container,
    MetadataReleaseExecutionPlan MetadataPlan)
{
    public static string ComputePlanHash(WorkflowOperationRecord operation, CoordinatedReleaseStep gate)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            From(operation, gate), CoordinatedReleaseApprovalJsonContext.Default.CoordinatedReleaseApprovalBinding))).ToLowerInvariant();

    public static CoordinatedReleaseApprovalBinding From(WorkflowOperationRecord operation, CoordinatedReleaseStep gate)
    {
        var context = operation.CoordinatedRelease ?? throw new HumanApprovalRequiredException();
        return new CoordinatedReleaseApprovalBinding(operation.OperationId, operation.Kind, gate,
            operation.Audit.TenantId, context.RequesterTenantId, operation.Audit.RequestedBy,
            context.RequesterSubject, context.RequesterName, context.RequesterTenantClaimType,
            context.RequesterAuthenticationType, context.RequesterRoles,
            context.PackageId, context.TargetEnvironment, context.Container, context.MetadataPlan);
    }

    public static bool HasExecutionAuthority(WorkflowOperationRecord operation, CoordinatedReleaseStep gate)
    {
        var context = operation.CoordinatedRelease!;
        var evidence = gate == CoordinatedReleaseStep.ContainerRollout
            ? context.ContainerHumanApproval : context.DataHumanApproval;
        if (evidence is null) return false;

        return evidence.Version == CoordinatedReleaseHumanApproval.CurrentVersion
            && evidence.WorkflowId == operation.OperationId && evidence.Gate == gate
            && evidence.WorkflowVersion >= 0 && evidence.WorkflowVersion < operation.Version
            && evidence.TenantId == operation.Audit.TenantId && evidence.TenantId == context.RequesterTenantId
            && evidence.Channel == OperationHumanApprovalEvidence.InteractiveOidcChannel
            && !string.IsNullOrWhiteSpace(evidence.ActorId)
            && IsReference(evidence.SessionReference) && IsReference(evidence.CapabilityReference)
            && evidence.PlanHash == ComputePlanHash(operation, gate);
    }

    private static bool IsReference(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

internal sealed class CoordinatedReleaseHumanReview
{
    public required string WorkflowId { get; init; }
    public required long WorkflowVersion { get; init; }
    public required CoordinatedReleaseStep Gate { get; init; }
    public required string PlanHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string Capability { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CoordinatedReleaseApprovalBinding))]
[JsonSerializable(typeof(CoordinatedReleaseHumanReview))]
internal sealed partial class CoordinatedReleaseApprovalJsonContext : JsonSerializerContext;
