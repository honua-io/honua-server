// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Observability.Abstractions;

namespace Honua.Server.Features.Capabilities;

/// <summary>
/// Materializes the runtime providers consumed by the capability manifest behind one
/// domain-specific collaborator. This keeps provider discovery separate from document
/// composition and prevents the manifest service from growing with every backend family.
/// </summary>
internal sealed class CapabilityManifestRuntimeInventory(
    IEnumerable<IBatchComputeBackend> batchBackends,
    IEnumerable<IDeployBackend> deployBackends,
    IEnumerable<IWorkflowOperationStore> workflowOperationStores,
    IEnumerable<IOpsAutonomyPolicyStore> opsAutonomyPolicyStores,
    IEnumerable<IFieldCollectionSyncStore> fieldCollectionSyncStores,
    IWebHostEnvironment hostEnvironment)
{
    public IReadOnlyList<IBatchComputeBackend> BatchBackends { get; } = batchBackends.ToArray();

    public IReadOnlyList<IDeployBackend> DeployBackends { get; } = deployBackends.ToArray();

    public bool HasDurableOperationStore { get; } = workflowOperationStores.Any();

    public bool HasAutonomyPolicyStore { get; } = opsAutonomyPolicyStores.Any();

    public bool HasFieldCollectionSyncStore { get; } = fieldCollectionSyncStores.Any();

    public string EnvironmentName { get; } = hostEnvironment.EnvironmentName;
}
