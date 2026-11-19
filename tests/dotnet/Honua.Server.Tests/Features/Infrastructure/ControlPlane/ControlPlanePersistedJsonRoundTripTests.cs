// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Orchestration;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class ControlPlanePersistedJsonRoundTripTests
{
    [UnitTest]
    public void StoredDeployApprovalWithoutTenant_ReadsBack()
    {
        var approval = new DeployHumanApproval
        {
            WorkflowId = "deploy-roundtrip",
            WorkflowVersion = 3,
            Action = "submit",
            TenantId = null,
            ActorId = "oidc:person-b",
            Channel = OperationHumanApprovalEvidence.InteractiveOidcChannel,
            SessionReference = new string('a', 64),
            CapabilityReference = new string('b', 64),
            PlanHash = new string('c', 64),
            ApprovedAt = DateTimeOffset.UnixEpoch.AddDays(1),
        };
        var record = new WorkflowOperationRecord
        {
            OperationId = "deploy-roundtrip",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Submitted,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            Audit = new OperationAuditInfo
            {
                RequestedBy = "oidc:person-a",
                DeployHumanApproval = approval,
            },
        };

        var json = JsonSerializer.Serialize(record, ControlPlaneJsonContext.Default.WorkflowOperationRecord);
        var read = JsonSerializer.Deserialize(json, ControlPlaneJsonContext.Default.WorkflowOperationRecord);

        Assert.Equal(approval, read?.Audit.DeployHumanApproval);
    }

    [UnitTheory]
    [InlineData(nameof(ControlPlaneJsonContext))]
    [InlineData(nameof(OperationProposalJsonContext))]
    [InlineData(nameof(DeploymentJsonContext))]
    [InlineData(nameof(PublishingJsonContext))]
    [InlineData(nameof(OrchestrationJsonContext))]
    public void PersistedControlPlaneGraph_HasNoRequiredNullableMembers(string contextName)
    {
        JsonSerializerContext context = contextName switch
        {
            nameof(ControlPlaneJsonContext) => ControlPlaneJsonContext.Default,
            nameof(OperationProposalJsonContext) => OperationProposalJsonContext.Default,
            nameof(DeploymentJsonContext) => DeploymentJsonContext.Default,
            nameof(PublishingJsonContext) => PublishingJsonContext.Default,
            nameof(OrchestrationJsonContext) => OrchestrationJsonContext.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(contextName)),
        };
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, context.Options.DefaultIgnoreCondition);

        Assert.Empty(RequiredNullableMembers(context));
    }

    // A null value is omitted on write under WhenWritingNull, so a required nullable member can never be read back.
    private static List<string> RequiredNullableMembers(JsonSerializerContext context)
    {
        var nullability = new NullabilityInfoContext();
        var offenders = new List<string>();
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>(SerializableRoots(context.GetType()));
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type) || context.GetTypeInfo(type) is not { } info)
            {
                continue;
            }

            offenders.AddRange(info.Properties
                .Where(property => property.IsRequired && IsNullable(property, nullability))
                .Select(property => $"{type.Name}.{property.Name}"));
            foreach (var next in Reachable(info))
            {
                pending.Enqueue(next);
            }
        }

        return offenders;
    }

    private static IEnumerable<Type> SerializableRoots(Type contextType) =>
        contextType.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType == typeof(JsonSerializableAttribute))
            .Select(attribute => (Type)attribute.ConstructorArguments[0].Value!);

    private static IEnumerable<Type> Reachable(JsonTypeInfo info)
    {
        var types = info.Properties.Select(property => property.PropertyType)
            .Concat(info.PolymorphismOptions?.DerivedTypes.Select(derived => derived.DerivedType) ?? [])
            .Append(info.ElementType)
            .Append(info.KeyType)
            .OfType<Type>();
        return types.Select(type => Nullable.GetUnderlyingType(type) ?? type);
    }

    private static bool IsNullable(JsonPropertyInfo property, NullabilityInfoContext nullability) =>
        Nullable.GetUnderlyingType(property.PropertyType) is not null
        || (property.AttributeProvider is PropertyInfo member
            && nullability.Create(member).ReadState == NullabilityState.Nullable);
}
