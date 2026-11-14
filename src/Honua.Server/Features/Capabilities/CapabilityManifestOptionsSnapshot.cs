// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.ControlPlane;
using Honua.Import.FileImport;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Server.Features.Realtime;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Capabilities.Models;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Server.Features.Streaming;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Capabilities;

internal sealed class CapabilityManifestOptionsSnapshot
{
    public CapabilityManifestOptionsSnapshot(
        IOptions<LimitsOptions> limitsOptions,
        IOptions<FeatureStreamOptions> streamOptions,
        IOptions<RealtimeOptions> realtimeOptions,
        IOptions<ClientCertificateAuthenticationOptions> clientCertificateOptions,
        IOptions<ControlPlaneOptions> controlPlaneOptions,
        IOptions<FileUploadOptions> fileUploadOptions,
        IOptions<FileUploadSecurityOptions> fileUploadSecurityOptions,
        IOptions<GrpcOptions> grpcOptions,
        IOptions<AlertOptions> alertOptions,
        IOptions<RbacOptions> rbacOptions,
        IOptions<CapabilityFlagOptions> capabilityFlagOptions,
        IOptions<CapabilityManifestFeatureOptions> manifestFeatureOptions,
        IOptions<TenantSchemaOptions> tenantSchemaOptions,
        IOptions<TenantContextOptions> tenantContextOptions,
        DeploymentCapabilityProfile deploymentProfile,
        DeploymentIdentity deploymentIdentity,
        IExecutionJobStore? executionJobStore = null,
        IJobQueue? jobQueue = null,
        Honua.Core.Features.FileImport.Abstractions.IBackgroundImportRunnerInfo? backgroundImportRunner = null,
        IOptions<ImportObjectStoreOptions>? objectStoreOptions = null,
        IOperationProposalStore? proposalStore = null)
    {
        BackgroundImportRunner = backgroundImportRunner;
        ObjectStoreConfigured = objectStoreOptions?.Value.AllowedBuckets?
            .Any(static bucket => !string.IsNullOrWhiteSpace(bucket)) == true;
        ArgumentNullException.ThrowIfNull(limitsOptions);
        ArgumentNullException.ThrowIfNull(streamOptions);
        ArgumentNullException.ThrowIfNull(realtimeOptions);
        ArgumentNullException.ThrowIfNull(clientCertificateOptions);
        ArgumentNullException.ThrowIfNull(controlPlaneOptions);
        ArgumentNullException.ThrowIfNull(fileUploadOptions);
        ArgumentNullException.ThrowIfNull(fileUploadSecurityOptions);
        ArgumentNullException.ThrowIfNull(grpcOptions);
        ArgumentNullException.ThrowIfNull(alertOptions);
        ArgumentNullException.ThrowIfNull(rbacOptions);
        ArgumentNullException.ThrowIfNull(capabilityFlagOptions);
        ArgumentNullException.ThrowIfNull(manifestFeatureOptions);
        ArgumentNullException.ThrowIfNull(tenantSchemaOptions);
        ArgumentNullException.ThrowIfNull(tenantContextOptions);
        ArgumentNullException.ThrowIfNull(deploymentProfile);
        ArgumentNullException.ThrowIfNull(deploymentIdentity);

        Limits = limitsOptions.Value;
        Streaming = streamOptions.Value;
        Realtime = realtimeOptions.Value;
        ClientCertificate = clientCertificateOptions.Value;
        ControlPlane = controlPlaneOptions.Value;
        FileUpload = fileUploadOptions.Value;
        FileUploadSecurity = fileUploadSecurityOptions.Value;
        Grpc = grpcOptions.Value;
        Alerts = alertOptions.Value;
        Rbac = rbacOptions.Value;
        ExperimentalCapabilityFlags = capabilityFlagOptions.Value;
        ManifestFromRegistry = manifestFeatureOptions.Value.FromRegistry;
        TenantSchemaRoutingEnabled = tenantSchemaOptions.Value.Enabled;
        TenantContextEnabled = tenantContextOptions.Value.Enabled;
        DurableJobRuntimeAvailable = executionJobStore is not null && jobQueue is not null;
        ProposalStoreAvailable = UnavailableOperationProposalStore.IsDurable(proposalStore);
        DeploymentIdentity = deploymentIdentity;
        DeploymentProfile = new CapabilityManifestDeploymentProfile
        {
            Configured = deploymentProfile.IsConfigured,
            SchemaVersion = deploymentProfile.SchemaVersion,
            EnabledCapabilities = deploymentProfile.EnabledCapabilities.ToArray()
        };
    }

    public Honua.Core.Features.FileImport.Abstractions.IBackgroundImportRunnerInfo? BackgroundImportRunner { get; }

    public bool ObjectStoreConfigured { get; }

    public LimitsOptions Limits { get; }

    public FeatureStreamOptions Streaming { get; }

    public RealtimeOptions Realtime { get; }

    public ClientCertificateAuthenticationOptions ClientCertificate { get; }

    public ControlPlaneOptions ControlPlane { get; }

    public FileUploadOptions FileUpload { get; }

    public FileUploadSecurityOptions FileUploadSecurity { get; }

    public GrpcOptions Grpc { get; }

    public AlertOptions Alerts { get; }

    public RbacOptions Rbac { get; }

    public CapabilityFlagOptions ExperimentalCapabilityFlags { get; }

    public bool ManifestFromRegistry { get; }

    public bool TenantSchemaRoutingEnabled { get; }

    public bool TenantContextEnabled { get; }

    // jobs.runner is advertised only when both the durable store and a runnable queue exist, because a store without a queue would persist submissions that never drain.
    public bool DurableJobRuntimeAvailable { get; }

    // The proposal store is composed separately from the job store and queue, so it is reported on its own.
    public bool ProposalStoreAvailable { get; }

    public CapabilityManifestDeploymentProfile DeploymentProfile { get; }

    public DeploymentIdentity DeploymentIdentity { get; }
}
