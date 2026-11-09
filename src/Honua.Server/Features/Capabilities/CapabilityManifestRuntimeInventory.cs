// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Plugins.Abstractions;

namespace Honua.Server.Features.Capabilities;

internal sealed class CapabilityManifestRuntimeInventory(
    IEnumerable<IBatchComputeBackend> batchBackends,
    IEnumerable<IDeployBackend> deployBackends,
    IEnumerable<IWorkflowOperationStore> workflowOperationStores,
    IEnumerable<IOpsAutonomyPolicyStore> opsAutonomyPolicyStores,
    IEnumerable<IFieldCollectionSyncStore> fieldCollectionSyncStores,
    IEnumerable<IFeatureDataProvider> featureDataProviders,
    IEnumerable<IFeatureLockAuthorizer> featureLockAuthorizers,
    IWebHostEnvironment hostEnvironment,
    IServiceProviderIsService serviceInventory,
    IFeatureOutputFormatRegistry outputFormats,
    ManifestRoutedCapabilityIndex routedCapabilities)
{
    public ManifestRoutedCapabilityIndex RoutedCapabilities { get; } = routedCapabilities;

    public IReadOnlyList<IBatchComputeBackend> BatchBackends { get; } = batchBackends.ToArray();

    public IReadOnlyList<IDeployBackend> DeployBackends { get; } = deployBackends.ToArray();

    public bool HasDurableOperationStore { get; } = workflowOperationStores.Any();

    public bool HasDurableProposalStore { get; init; }

    public bool HasAutonomyPolicyStore { get; } = opsAutonomyPolicyStores.Any();

    public bool HasFieldCollectionSyncStore { get; } = fieldCollectionSyncStores.Any();

    public bool FeatureLockAuthorizerConfigured { get; } = featureLockAuthorizers
        .Any(static authorizer => authorizer is not Collaboration.FeatureLocks.FailClosedFeatureLockAuthorizer);

    public IReadOnlySet<string> FeatureDataProviders { get; } = featureDataProviders
        .Select(static provider => provider.ProviderName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public string EnvironmentName { get; } = hostEnvironment.EnvironmentName;

    public bool HasFileImportService { get; } = serviceInventory.IsService(typeof(IFileImportService));

    public IReadOnlyCollection<PluginOutputFormatDescriptor> ActiveOutputFormats => outputFormats.HasFormats
        ? outputFormats.AdvertisedFormats : [];
}
