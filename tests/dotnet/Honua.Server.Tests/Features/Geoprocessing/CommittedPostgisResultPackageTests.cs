// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
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
