// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.ProcessRelease;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing;

internal sealed class CustomCodeJobSubmissionGate
{
    private readonly ILogger<GeoprocessingJobService> _logger;
    private readonly IScopedJobTokenIssuer? _scopedJobTokenIssuer;
    private readonly IOptionsMonitor<CustomCodeOptions>? _customCodeOptions;
    private readonly CustomCodeSubmitCoordinator? _coordinator;
    private readonly GpProcessReleaseAdmission? _releaseAdmission;
    private readonly IExecutionJobDefinitionRegistry? _workloads;
    private readonly ICloudFileStorage? _artifactStorage;

    public CustomCodeJobSubmissionGate(
        ILogger<GeoprocessingJobService> logger,
        IScopedJobTokenIssuer? scopedJobTokenIssuer = null,
        IOptionsMonitor<CustomCodeOptions>? customCodeOptions = null,
        ICustomCodeCommitSignatureVerifier? customCodeSignatureVerifier = null,
        GpProcessReleaseAdmission? releaseAdmission = null,
        IExecutionJobDefinitionRegistry? workloads = null,
        ICloudFileStorage? artifactStorage = null)
    {
        _logger = logger;
        _artifactStorage = artifactStorage;
        _releaseAdmission = releaseAdmission;
        _workloads = workloads;
        _scopedJobTokenIssuer = scopedJobTokenIssuer;
        _customCodeOptions = customCodeOptions;
        _coordinator = scopedJobTokenIssuer is null
            ? null
            : new CustomCodeSubmitCoordinator(scopedJobTokenIssuer, customCodeSignatureVerifier);
    }

    public async Task<(CustomCodeOwnerScope OwnerScope, string Token, ExecutionJobDefinition? Workload)> ValidateMintAndInjectAsync(
        string jobId,
        ClaimsPrincipal principal,
        Dictionary<string, string> specParams,
        ExecutionJobDefinition? workload,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null || _customCodeOptions is null)
        {
            throw new GeoprocessingValidationException(
                "Custom-code geoprocessing is not enabled on this server (no scoped-job token issuer is configured).");
        }

        if (_artifactStorage?.Provider is not (CloudStorageProvider.AwsS3 or CloudStorageProvider.AzureBlob))
        {
            throw new GeoprocessingValidationException("Custom-code jobs require durable cloud storage for the authenticated artifact broker.");
        }

        // Open-policy gate (PA-196): an unrestricted-repo execution surface requires admin
        // elevation. A non-admin caller is rejected even when Open is configured so that
        // arbitrary code execution from any HTTPS repo is never reachable by a normal user.
        if (_customCodeOptions.CurrentValue.RepoPolicy == CustomCodeRepoPolicy.Open
            && !principal.IsInRole("admin"))
        {
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        }

        // Release pinning (honua-server#6221): a submission that names a process is resolved
        // against its live binding before the custom-code parameters are validated, so the
        // validated repo/ref/entrypoint are the release's and the job keeps them on every retry.
        if (_releaseAdmission is not null)
        {
            await _releaseAdmission.ApplyAsync(specParams, cancellationToken).ConfigureAwait(false);
        }

        else if (specParams.ContainsKey(GpProcessReleaseAdmission.ProcessParam))
        {
            throw new GeoprocessingValidationException("Process release admission is not configured on this server.");
        }

        // Select the registered queue/backend configuration for this exact release before
        // minting credentials. A first-match custom-code workload can belong to another image.
        if (specParams.ContainsKey(GpProcessReleaseAdmission.ReleaseLockDigestParam))
        {
            var definitions = _workloads is null
                ? Array.Empty<ExecutionJobDefinition>()
                : await _workloads.ListAsync(cancellationToken).ConfigureAwait(false);
            workload = definitions.FirstOrDefault(definition =>
                definition.Kind == ExecutionJobKind.Geoprocessing
                && definition.TargetKind == BatchComputeTargetKind.AwsBatch
                && string.Equals(definition.RuntimeProfile, CustomCodeJobContract.RuntimeProfile, StringComparison.Ordinal)
                && specParams.TryGetValue(GpProcessReleaseAdmission.WorkerImageParam, out var image)
                && string.Equals(definition.ArtifactReference?.Trim(), image, StringComparison.Ordinal)
                && specParams.TryGetValue(GpProcessReleaseAdmission.AwsBatchJobDefinitionParam, out var arn)
                && definition.Parameters.Any(parameter =>
                    (parameter.Key == GpProcessReleaseAdmission.AwsBatchJobDefinitionParam
                        || parameter.Key.StartsWith(GpProcessReleaseAdmission.AwsBatchJobDefinitionParam + ".", StringComparison.Ordinal))
                    && string.Equals(parameter.Value?.Trim(), arn, StringComparison.Ordinal)));
            if (workload is null)
            {
                throw new GeoprocessingValidationException("The active process release requires a registered AWS Batch workload with its pinned image and job-definition revision.");
            }

            GpProcessReleaseAdmission.ApplyWorkloadManifest(specParams, workload.Parameters);
        }

        try
        {
            var result = await _coordinator.ValidateMintAndInjectAsync(
                jobId, principal, specParams, _customCodeOptions.CurrentValue, cancellationToken).ConfigureAwait(false);
            return (result.OwnerScope, result.Token, workload);
        }
        catch (CustomCodeSubmitRejectedException ex)
        {
            GeoprocessingServiceLog.DeclaredScopeRejected(_logger, ex.Message);
            throw new GeoprocessingValidationException(ex.Message);
        }
    }

    // The callback token is revoked when submission rolls back so the credential does not outlive the job.
    public async Task TryRevokeTokenAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || _scopedJobTokenIssuer is null)
        {
            return;
        }

        try
        {
            await _scopedJobTokenIssuer.RevokeAsync(token, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentionally broad: this is the documented best-effort revocation path (see
            // the XML doc above) — a revoke failure must not fail job submission/cleanup, but
            // it is logged so an un-revoked token is diagnosable rather than silently dropped.
            GeoprocessingServiceLog.CustomCodeTokenRevokeFailed(_logger, ex);
        }
    }
}
