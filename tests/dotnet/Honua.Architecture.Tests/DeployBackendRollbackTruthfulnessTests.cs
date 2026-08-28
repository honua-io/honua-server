// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Runtime.CompilerServices;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the complete deploy-backend roster against advertising rollback from the shared
/// hand-off implementation. Concrete rollback backends must own both the request and observation
/// methods that can settle the operation; hand-off backends must advertise false.
/// </summary>
public sealed class DeployBackendRollbackTruthfulnessTests
{
    private static readonly HashSet<string> HandOffBackendTypes =
    [
        "KubernetesGitOpsDeployBackend",
        "AwsEcsGitOpsDeployBackend",
        "AzureContainerAppsGitOpsDeployBackend",
    ];

    [Fact]
    public async Task EveryDeployBackend_AdvertisesRollbackOnlyWithConcreteRevertAndObservation()
    {
        var serverAssembly = Assembly.Load("Honua.Server");
        var backendTypes = serverAssembly.GetReferencedAssemblies()
            .Select(Assembly.Load)
            .Append(serverAssembly)
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => !type.IsAbstract && typeof(IDeployBackend).IsAssignableFrom(type))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(9, backendTypes.Length);

        foreach (var backendType in backendTypes)
        {
            var backend = (IDeployBackend)RuntimeHelpers.GetUninitializedObject(backendType);
            var capabilities = await backend.GetCapabilitiesAsync();
            var rollbackMethod = backendType.GetMethod(nameof(IDeployBackend.RollbackAsync));
            var observeMethod = backendType.GetMethod(nameof(IDeployBackend.ObserveAsync));

            Assert.NotNull(rollbackMethod);
            Assert.NotNull(observeMethod);
            if (capabilities.SupportsRollback)
            {
                Assert.Equal(backendType, rollbackMethod!.DeclaringType);
                Assert.Equal(backendType, observeMethod!.DeclaringType);
            }
            else
            {
                Assert.Contains(backendType.Name, HandOffBackendTypes);
                var loggerType = typeof(NullLogger<>).MakeGenericType(backendType);
                var logger = Activator.CreateInstance(loggerType);
                var handOffBackend = (IDeployBackend)Activator.CreateInstance(
                    backendType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: [logger],
                    culture: null)!;
                var rollback = await handOffBackend.RollbackAsync(CreateFakeOperation(handOffBackend));

                Assert.Equal(WorkflowOperationStatus.ManualInterventionRequired, rollback.Status);
                Assert.NotEqual(WorkflowOperationStatus.RollbackRequested, rollback.Status);
            }
        }
    }

    private static WorkflowOperationRecord CreateFakeOperation(IDeployBackend backend)
        => new()
        {
            OperationId = "architecture-rollback-contract",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            CurrentPhase = "health gate failed",
            Audit = new OperationAuditInfo
            {
                RequestedBy = "architecture-test",
                Reason = "rollback truthfulness",
                IdempotencyKey = "architecture-rollback-contract",
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "architecture-target",
                TargetKind = backend.TargetKind,
                Backend = backend.BackendName,
                Environment = "test",
                TargetName = "architecture-target",
                DesiredRevision = "new",
                CurrentRevision = "old",
            },
        };
}
