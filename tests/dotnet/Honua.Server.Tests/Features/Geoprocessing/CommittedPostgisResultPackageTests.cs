// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class CommittedPostgisResultPackageTests
{
    private const string ConnectionId = "11111111-1111-1111-1111-111111111111";
    private const string Prefix = "honua.geoprocessing.step.0.";
    private const string Fingerprint =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Receipt =
        "{\"processId\":\"sink.external-postgis\",\"schema\":\"results\",\"table\":\"output\","
        + "\"batchId\":\"batch-1\",\"committed\":true,\"featuresWritten\":2,\"featuresRejected\":0,"
        + "\"endpointFingerprint\":\"" + Fingerprint + "\"}";

    private const string ReceiptWithoutFingerprint =
        "{\"processId\":\"sink.external-postgis\",\"schema\":\"results\",\"table\":\"output\","
        + "\"batchId\":\"batch-1\",\"committed\":true,\"featuresWritten\":2,\"featuresRejected\":0}";

    [UnitTest]
    public void SuccessfulCommittedSink_ProjectsOnlyRegisteredDestinationIdentity()
    {
        var artifact = Package(Job(Receipt)).Artifacts.Should().ContainSingle().Subject;

        artifact.Kind.Should().Be(ArtifactKind.FeatureLayer);
        artifact.Uri.Should().StartWith("data:application/json;base64,");
        artifact.ContentType.Should().Be("application/json", "the committed sink artifact is a receipt, not GeoJSON geometry");
        artifact.Metadata.Should().ContainKey("connectionId").WhoseValue.Should().Be(ConnectionId);
        artifact.Metadata.Should().ContainKey("schema").WhoseValue.Should().Be("results");
        artifact.Metadata.Should().ContainKey("table").WhoseValue.Should().Be("output");
        artifact.Metadata.Should().ContainKey(ConnectionEndpointFingerprint.MetadataKey).WhoseValue.Should().Be(Fingerprint);
        artifact.Metadata.Values.Should().NotContain(value => value.Contains("Host=", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> UntrustedDestinationCases()
    {
        yield return ["missing committed receipt", Job(Receipt) with { CommittedEffectReferences = [] }];
        yield return ["failed after commit", Job(Receipt) with { Status = ExecutionJobStatus.Failed }];
        yield return ["cancelled after commit", Job(Receipt) with { Status = ExecutionJobStatus.Cancelled }];
        yield return ["missing connection id", Job(Receipt, connectionId: null)];
        yield return ["name-only connection", Job(Receipt, connectionId: null, connectionName: "registered-name")];
        yield return ["two connection identities", Job(Receipt, connectionName: "registered-name")];
        yield return ["invalid connection id", Job(Receipt, connectionId: "not-a-guid")];
        yield return ["mismatched destination", Job(Receipt.Replace("\"output\"", "\"other\"", StringComparison.Ordinal))];
        yield return ["not committed", Job(Receipt.Replace("\"committed\":true", "\"committed\":false", StringComparison.Ordinal))];
        yield return ["partial receipt", Job(Receipt[..^1] + ",\"partial\":true}")];
        yield return ["wrong process", Job(Receipt.Replace("sink.external-postgis", "sink.honua-layer", StringComparison.Ordinal))];
        yield return ["duplicate destination", Job(Receipt[..^1] + ",\"table\":\"output\"}")];
        yield return ["malformed receipt", Job("not-json")];
        yield return ["malformed base64", WithReference(Job(Receipt), "data:application/json;base64,%%%")];
        yield return ["oversized receipt", WithReference(Job(Receipt),
            "data:application/json;base64," + new string('A', 4096))];
        yield return ["multiple committed effects", Job(Receipt) with
        {
            CommittedEffectReferences = [ToDataUri(Receipt), ToDataUri(Receipt)]
        }];
        yield return ["wrong durable process", WithProcess(Job(Receipt), "sink.honua-layer")];
        yield return ["multiple artifact slots", Job(Receipt) with
        {
            ArtifactReferences = [ToDataUri(Receipt), ToDataUri(Receipt)]
        }];
        yield return ["missing endpoint fingerprint", Job(ReceiptWithoutFingerprint)];
        yield return ["reserved schema", Job(ReceiptWithSchema("pg_catalog"), schema: "pg_catalog")];
    }

    [Theory]
    [MemberData(nameof(UntrustedDestinationCases))]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void UntrustedDestination_RefusesPublicationMetadata(string reason, ExecutionJobRecord job)
    {
        var package = Package(job);
        package.Artifacts.Should().OnlyContain(artifact =>
            !artifact.Metadata.ContainsKey("connectionId") &&
            !artifact.Metadata.ContainsKey("schema") &&
            !artifact.Metadata.ContainsKey("table") &&
            !artifact.Metadata.ContainsKey(ConnectionEndpointFingerprint.MetadataKey), reason);
    }

    [UnitTest]
    public async Task CommittedSinkVerifier_RequiresCompletedOwnedExactDestination()
    {
        var original = Job(Receipt) with
        {
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                TenantId = "tenant-a",
                SubmitterSecurityContext = new JobSecurityContext("alice", "tenant-a", [])
                {
                    OwnerActorId = "alice"
                }
            }
        };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync("job-1", Arg.Any<CancellationToken>()).Returns(call => original);
        var catalog = Substitute.For<IProcessCatalog>();

        async Task Verify(
            string artifactId = "job-1:artifact:1",
            string schema = "results",
            string table = "output",
            string actor = "alice",
            bool administrator = false,
            string tenant = "tenant-a",
            string? expectedOwner = null)
            => _ = await CommittedSinkPublicationVerifier.VerifyAsync(
                store, catalog, "job-1", artifactId, Guid.Parse(ConnectionId), schema, table,
                Fingerprint, tenant, actor, administrator, expectedOwner, CancellationToken.None);

        await Verify();
        await FluentActions.Invoking(() => Verify(artifactId: "job-1:artifact:2"))
            .Should().ThrowAsync<LayerPublishingException>();
        await FluentActions.Invoking(() => Verify(schema: "other"))
            .Should().ThrowAsync<LayerPublishingException>();
        await FluentActions.Invoking(() => Verify(table: "other"))
            .Should().ThrowAsync<LayerPublishingException>();
        await FluentActions.Invoking(() => Verify(actor: "bob"))
            .Should().ThrowAsync<LayerPublishingException>();
        await FluentActions.Invoking(() => Verify(tenant: "tenant-b"))
            .Should().ThrowAsync<LayerPublishingException>();
        await FluentActions.Invoking(() => Verify(expectedOwner: "bob"))
            .Should().ThrowAsync<LayerPublishingException>();
        await Verify(actor: "admin", administrator: true);

        store.GetAsync("job-1", Arg.Any<CancellationToken>())
            .Returns(call => original with { Status = ExecutionJobStatus.Failed });
        await FluentActions.Invoking(() => Verify())
            .Should().ThrowAsync<LayerPublishingException>();
    }

    [UnitTest]
    public async Task ServicePublishPreparation_RejectsGenericSpoofAndRevalidatesApprovedReplay()
    {
        var sourceJob = Job(Receipt) with
        {
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                TenantId = "tenant-a",
                SubmitterSecurityContext = new JobSecurityContext("alice", "tenant-a", [])
                {
                    OwnerActorId = "alice"
                }
            }
        };
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(sourceJob.OperationId, Arg.Any<CancellationToken>()).Returns(sourceJob);
        var executor = new ServicePublishExecutor(
            Substitute.For<ILayerPublishingService>(),
            Substitute.For<ISecureConnectionResolver>(),
            Substitute.For<IMetadataV2GraphProvider>(),
            TimeProvider.System,
            sourceProof: new CommittedSinkPublicationSources(jobs, Substitute.For<IProcessCatalog>()));
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["schema"] = "results",
            ["table"] = "output",
            ["layerName"] = "Output",
            [VerifiedCommittedSinkPublication.SourceJobIdKey] = sourceJob.OperationId,
            [VerifiedCommittedSinkPublication.SourceArtifactIdKey] = "job-1:artifact:1"
        };
        var request = new OperationRequest
        {
            OperationId = ServicePublishOperation.OperationId,
            ConnectionId = ConnectionId,
            Parameters = parameters
        };
        var alice = new OperationPolicyContext { PrincipalId = "alice", TenantId = "tenant-a" };

        var fingerprintOnly = request with
        {
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["schema"] = "results",
                ["table"] = "output",
                ["layerName"] = "Output",
                [ConnectionEndpointFingerprint.MetadataKey] = Fingerprint
            }
        };
        await FluentActions.Invoking(() => executor.PrepareAsync(fingerprintOnly, alice))
            .Should().ThrowAsync<InvalidOperationException>();

        var prepared = await executor.PrepareAsync(request, alice);
        prepared.VerifiedCommittedSinkPublication.Should().NotBeNull();
        prepared.Parameters[ConnectionEndpointFingerprint.MetadataKey].Should().Be(Fingerprint);
        prepared.VerifiedCommittedSinkPublication!.SourceOwnerActorId.Should().Be("alice");
        var forgedJson = JsonNode.Parse(JsonSerializer.Serialize(fingerprintOnly,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        forgedJson["verifiedCommittedSinkPublication"] = JsonSerializer.SerializeToNode(
            prepared.VerifiedCommittedSinkPublication);
        var deserializedForgery = JsonSerializer.Deserialize<OperationRequest>(forgedJson.ToJsonString(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        deserializedForgery.VerifiedCommittedSinkPublication.Should().BeNull();
        await FluentActions.Invoking(() => executor.PrepareAsync(deserializedForgery, alice))
            .Should().ThrowAsync<InvalidOperationException>();

        var forged = request with
        {
            Parameters = new Dictionary<string, string?>(parameters, StringComparer.Ordinal)
            {
                [VerifiedCommittedSinkPublication.PublisherActorIdKey] = "bob",
                [VerifiedCommittedSinkPublication.PublisherWasAdministratorKey] = "true"
            }
        };
        await FluentActions.Invoking(() => executor.PrepareAsync(forged,
                new OperationPolicyContext { PrincipalId = "bob", TenantId = "tenant-a" }))
            .Should().ThrowAsync<InvalidOperationException>();

        var admin = new OperationPolicyContext
        {
            PrincipalId = "administrator",
            TenantId = "tenant-a",
            Roles = ["admin"]
        };
        var adminPrepared = await executor.PrepareAsync(request, admin);
        adminPrepared.VerifiedCommittedSinkPublication!.PublisherWasAdministrator.Should().BeTrue();
        var durable = ServicePublishApprovalPayload.From(adminPrepared, admin);
        var serialized = JsonSerializer.Serialize(durable,
            ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload);
        var restored = JsonSerializer.Deserialize(serialized,
            ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload)!;
        var replayRequest = restored.ToOperationRequest();
        replayRequest.VerifiedCommittedSinkPublication.Should().BeNull();
        var replayContext = admin with { Roles = [], ApprovedProposalId = "approved-1" };
        var replayPrepared = await executor.PrepareAsync(replayRequest, replayContext);
        replayPrepared.VerifiedCommittedSinkPublication!.PublisherWasAdministrator.Should().BeTrue();

        await FluentActions.Invoking(() => executor.PrepareAsync(replayRequest,
                replayContext with { PrincipalId = "mallory" }))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => executor.PrepareAsync(replayRequest,
                replayContext with { TenantId = "tenant-b" }))
            .Should().ThrowAsync<LayerPublishingException>();
        await FluentActions.Invoking(() => executor.PrepareAsync(replayRequest with
        {
            Parameters = new Dictionary<string, string?>(replayRequest.Parameters, StringComparer.Ordinal)
            {
                ["table"] = "other"
            }
        }, replayContext))
            .Should().ThrowAsync<LayerPublishingException>();
        await FluentActions.Invoking(() => executor.PrepareAsync(replayRequest with
        {
            Parameters = new Dictionary<string, string?>(replayRequest.Parameters, StringComparer.Ordinal)
            {
                [VerifiedCommittedSinkPublication.SourceArtifactIdKey] = "job-1:artifact:2"
            }
        }, replayContext))
            .Should().ThrowAsync<LayerPublishingException>();
    }

    private static AnalysisResultPackage Package(ExecutionJobRecord job)
        => GeoprocessingResultPackageFactory.Create(job, Substitute.For<IProcessCatalog>());

    private static string ReceiptWithSchema(string schema)
        => Receipt.Replace("\"schema\":\"results\"", $"\"schema\":\"{schema}\"", StringComparison.Ordinal);

    private static ExecutionJobRecord Job(
        string receipt,
        string? connectionId = ConnectionId,
        string? connectionName = null,
        string schema = "results")
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "sink.external-postgis",
            [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = nameof(ArtifactKind.FeatureLayer),
            [Prefix + "schema"] = schema,
            [Prefix + "table"] = "output",
        };
        if (connectionId is not null)
        {
            parameters[Prefix + "connectionId"] = connectionId;
        }
        if (connectionName is not null)
        {
            parameters[Prefix + "connectionName"] = connectionName;
        }

        var reference = ToDataUri(receipt);
        return new ExecutionJobRecord
        {
            OperationId = "job-1",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = [reference],
            CommittedEffectReferences = [reference],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test",
                WorkloadName = "sink.external-postgis",
                Parameters = parameters
            }
        };
    }

    private static string ToDataUri(string json)
        => "data:application/json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static ExecutionJobRecord WithReference(ExecutionJobRecord job, string reference)
        => job with { ArtifactReferences = [reference], CommittedEffectReferences = [reference] };

    private static ExecutionJobRecord WithProcess(ExecutionJobRecord job, string process)
    {
        var parameters = new Dictionary<string, string>(job.Spec.Parameters, StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = process
        };
        return job with { Spec = job.Spec with { Parameters = parameters } };
    }
}
