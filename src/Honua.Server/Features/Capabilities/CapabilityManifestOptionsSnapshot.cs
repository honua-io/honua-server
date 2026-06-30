// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Alerts.Domain;
using Honua.ControlPlane;
using Honua.Import.FileImport;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Server.Features.Streaming;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Capabilities;

/// <summary>
/// Immutable snapshot that collapses the many configuration option bundles the capability
/// manifest reads into a single cohesive dependency. Capturing the bound option values once
/// removes the option-injection noise from <see cref="CapabilityManifestService"/> so its
/// constructor reflects its real collaborators rather than configuration plumbing.
/// </summary>
internal sealed class CapabilityManifestOptionsSnapshot
{
    public CapabilityManifestOptionsSnapshot(
        IOptions<LimitsOptions> limitsOptions,
        IOptions<FeatureStreamOptions> streamOptions,
        IOptions<FeatureChangeEventOptions> eventOptions,
        IOptions<ClientCertificateAuthenticationOptions> clientCertificateOptions,
        IOptions<ControlPlaneOptions> controlPlaneOptions,
        IOptions<FileUploadOptions> fileUploadOptions,
        IOptions<FileUploadSecurityOptions> fileUploadSecurityOptions,
        IOptions<GrpcOptions> grpcOptions,
        IOptions<AlertOptions> alertOptions,
        IOptions<RbacOptions> rbacOptions)
    {
        ArgumentNullException.ThrowIfNull(limitsOptions);
        ArgumentNullException.ThrowIfNull(streamOptions);
        ArgumentNullException.ThrowIfNull(eventOptions);
        ArgumentNullException.ThrowIfNull(clientCertificateOptions);
        ArgumentNullException.ThrowIfNull(controlPlaneOptions);
        ArgumentNullException.ThrowIfNull(fileUploadOptions);
        ArgumentNullException.ThrowIfNull(fileUploadSecurityOptions);
        ArgumentNullException.ThrowIfNull(grpcOptions);
        ArgumentNullException.ThrowIfNull(alertOptions);
        ArgumentNullException.ThrowIfNull(rbacOptions);

        Limits = limitsOptions.Value;
        Streaming = streamOptions.Value;
        FeatureChangeEvents = eventOptions.Value;
        ClientCertificate = clientCertificateOptions.Value;
        ControlPlane = controlPlaneOptions.Value;
        FileUpload = fileUploadOptions.Value;
        FileUploadSecurity = fileUploadSecurityOptions.Value;
        Grpc = grpcOptions.Value;
        Alerts = alertOptions.Value;
        Rbac = rbacOptions.Value;
    }

    public LimitsOptions Limits { get; }

    public FeatureStreamOptions Streaming { get; }

    public FeatureChangeEventOptions FeatureChangeEvents { get; }

    public ClientCertificateAuthenticationOptions ClientCertificate { get; }

    public ControlPlaneOptions ControlPlane { get; }

    public FileUploadOptions FileUpload { get; }

    public FileUploadSecurityOptions FileUploadSecurity { get; }

    public GrpcOptions Grpc { get; }

    public AlertOptions Alerts { get; }

    public RbacOptions Rbac { get; }
}
