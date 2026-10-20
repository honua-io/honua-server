// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Geoprocessing;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Re-reads the completed sink job and its committed receipt before a publication can use the
/// external-schema exception. Caller-supplied metadata is only an equality claim, never proof.
/// </summary>
internal static class CommittedSinkPublicationVerifier
{
    internal static Task<VerifiedCommittedSinkPublication> RevalidateAsync(
        IExecutionJobStore jobStore,
        IProcessCatalog processCatalog,
        VerifiedCommittedSinkPublication claim,
        Guid connectionId,
        string schema,
        string table,
        string? endpointFingerprint,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (connectionId != claim.ConnectionId ||
            !string.Equals(schema, claim.Schema, StringComparison.Ordinal) ||
            !string.Equals(table, claim.Table, StringComparison.Ordinal) ||
            !string.Equals(endpointFingerprint, claim.EndpointFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(tenantId, claim.TenantId, StringComparison.Ordinal))
        {
            throw Refused();
        }

        return VerifyAsync(jobStore, processCatalog, claim.SourceJobId, claim.SourceArtifactId,
            claim.ConnectionId, claim.Schema, claim.Table, claim.EndpointFingerprint, claim.TenantId,
            claim.PublisherActorId, claim.PublisherWasAdministrator, claim.SourceOwnerActorId,
            cancellationToken);
    }

    internal static async Task<VerifiedCommittedSinkPublication> VerifyAsync(
        IExecutionJobStore jobStore,
        IProcessCatalog processCatalog,
        string sourceJobId,
        string sourceArtifactId,
        Guid connectionId,
        string schema,
        string table,
        string? endpointFingerprint,
        string? tenantId,
        string publisherActorId,
        bool publisherIsAdministrator,
        string? expectedSourceOwnerActorId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceJobId) || sourceJobId.Length > 256 ||
            string.IsNullOrWhiteSpace(sourceArtifactId) || sourceArtifactId.Length > 256 ||
            connectionId == Guid.Empty || string.IsNullOrWhiteSpace(schema) ||
            string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(publisherActorId))
        {
            throw Refused();
        }

        var job = await jobStore.GetAsync(sourceJobId, cancellationToken).ConfigureAwait(false);
        if (job is null ||
            !string.Equals(job.OperationId, sourceJobId, StringComparison.Ordinal) ||
            job.Status != ExecutionJobStatus.Succeeded)
        {
            throw Refused();
        }

        var security = job.Audit.SubmitterSecurityContext;
        var sourceOwner = security?.OwnerActorId;
        if (security is null || string.IsNullOrWhiteSpace(sourceOwner) ||
            !string.Equals(security.TenantId, tenantId, StringComparison.Ordinal) ||
            (job.Audit.TenantId is not null &&
             !string.Equals(job.Audit.TenantId, security.TenantId, StringComparison.Ordinal)) ||
            !string.Equals(job.Audit.RequestedBy, sourceOwner, StringComparison.Ordinal) ||
            (expectedSourceOwnerActorId is not null &&
             !string.Equals(expectedSourceOwnerActorId, sourceOwner, StringComparison.Ordinal)) ||
            (!string.Equals(sourceOwner, publisherActorId, StringComparison.Ordinal) &&
             !publisherIsAdministrator))
        {
            throw Refused();
        }

        AnalysisResultPackage package;
        try
        {
            package = GeoprocessingResultPackageFactory.Create(job, processCatalog);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Refused();
        }

        if (package.Status != GeoprocessingWorkflowStatus.Completed)
        {
            throw Refused();
        }

        var artifacts = package.Artifacts
            .Where(candidate => string.Equals(candidate.ArtifactId, sourceArtifactId, StringComparison.Ordinal))
            .ToArray();
        if (package.Artifacts.Count != 1 || artifacts.Length != 1 ||
            artifacts[0].Kind is not ArtifactKind.FeatureLayer)
        {
            throw Refused();
        }

        var metadata = artifacts[0].Metadata;
        if (!metadata.TryGetValue("connectionId", out var recordedConnectionId) ||
            !Guid.TryParse(recordedConnectionId, out var recordedGuid) ||
            recordedGuid != connectionId ||
            !metadata.TryGetValue("schema", out var recordedSchema) ||
            !string.Equals(recordedSchema, schema, StringComparison.Ordinal) ||
            !metadata.TryGetValue("table", out var recordedTable) ||
            !string.Equals(recordedTable, table, StringComparison.Ordinal) ||
            !metadata.TryGetValue(ConnectionEndpointFingerprint.MetadataKey, out var recordedFingerprint) ||
            !ConnectionEndpointFingerprint.IsWellFormed(recordedFingerprint) ||
            (endpointFingerprint is not null &&
             !string.Equals(recordedFingerprint, endpointFingerprint, StringComparison.OrdinalIgnoreCase)))
        {
            throw Refused();
        }

        return new VerifiedCommittedSinkPublication
        {
            SourceJobId = sourceJobId,
            SourceArtifactId = sourceArtifactId,
            ConnectionId = connectionId,
            Schema = schema,
            Table = table,
            EndpointFingerprint = recordedFingerprint,
            SourceOwnerActorId = sourceOwner,
            PublisherActorId = publisherActorId,
            PublisherWasAdministrator = publisherIsAdministrator,
            TenantId = tenantId
        };
    }

    private static LayerPublishingException Refused()
        => new(LayerPublishingErrorKind.Validation,
            "The requested publication is not bound to an authorized completed sink artifact.");
}
