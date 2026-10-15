// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing.Release;
using Xunit;

namespace Honua.Geoprocessing.UnitTests.Features.Geoprocessing.Release;

/// <summary>
/// The server-side lock reader/writer must agree byte-for-byte with the release repository's
/// Python digest (<c>tools/gp_process_release.py lock-digest</c>): the committed lock
/// <c>release/gp-process-locks/buffer-features/75154d7a4ebd.json</c> is the cross-implementation
/// test vector, and its file name is the first twelve hex characters of that digest.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public sealed class GpProcessReleaseLockTests
{
    // Verbatim copy of honua-release trunk release/gp-process-locks/buffer-features/75154d7a4ebd.json
    // (honua-release#618). Its canonical digest, computed by the Python tool, is the file name.
    private const string CommittedLock = """
        {
          "artifacts": [
            {
              "name": "buffer-features-1.1.0.tar.gz",
              "sha256": "sha256:7777777777777777777777777777777777777777777777777777777777777777",
              "sizeBytes": 18432
            }
          ],
          "compatibility": {
            "serverContractVersion": {
              "maximum": 4,
              "minimum": 3
            }
          },
          "configurationReferences": [
            {
              "name": "HONUA_DATA_BUCKET",
              "reference": "ref:environment/data-bucket"
            }
          ],
          "dependencyLock": {
            "path": "samples/gp-local-dev/uv.lock",
            "sha256": "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
          },
          "generatedAt": "2026-10-15T04:00:00Z",
          "process": {
            "contract": {
              "inputSchemaSha256": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
              "outputSchemaSha256": "sha256:2222222222222222222222222222222222222222222222222222222222222222"
            },
            "entrypoint": "buffer_features:execute",
            "name": "buffer-features",
            "runtime": "python",
            "version": "1.1.0"
          },
          "runtimeImage": {
            "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            "reference": "ghcr.io/honua-io/honua-gp-worker@sha256:0000000000000000000000000000000000000000000000000000000000000000"
          },
          "schema": "honua.gp-process-release-lock/v1",
          "source": {
            "descriptorPath": "samples/gp-local-dev/honua-gp.yaml",
            "repository": "honua-io/honua-samples",
            "sha": "ffffffffffffffffffffffffffffffffffffffff"
          },
          "targets": [
            {
              "chartVersion": "2026.1.0",
              "kind": "kubernetes-job",
              "workerImageDigest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            }
          ],
          "validation": {
            "receipts": [
              {
                "name": "golden-tests.junit.xml",
                "sha256": "sha256:9999999999999999999999999999999999999999999999999999999999999999",
                "status": "pass"
              }
            ]
          },
          "workflowVersion": "wf-2026.1-7"
        }
        """;

    private const string CommittedDigest = "sha256:75154d7a4ebdf830a0e68cfe5e2d7a15fbb58ceef02eba73fa6bc92e3ecad747";

    [Fact]
    public void Digest_MatchesTheReleaseRepositoryPythonDigest()
    {
        var lockDocument = GpProcessReleaseLock.Parse(CommittedLock);

        lockDocument.Digest.Should().Be(CommittedDigest);
        lockDocument.ShortDigest.Should().Be("75154d7a4ebd");
    }

    [Fact]
    public void CanonicalJson_HasSortedKeysNoWhitespaceAndRoundTrips()
    {
        var lockDocument = GpProcessReleaseLock.Parse(CommittedLock);

        var canonical = lockDocument.ToCanonicalJson();
        canonical.Should().StartWith("{\"artifacts\":[{\"name\":\"buffer-features-1.1.0.tar.gz\",\"sha256\":");
        canonical.Should().NotContain("\n").And.NotContain(": ");
        var reparsed = GpProcessReleaseLock.Parse(canonical);
        reparsed.ToCanonicalJson().Should().Be(canonical);
        reparsed.Digest.Should().Be(CommittedDigest);
        GpProcessReleaseLock.Parse(lockDocument.ToIndentedJson()).Digest.Should().Be(CommittedDigest);
    }

    [Fact]
    public void IndentedJson_MatchesTheCommittedFileFormatting()
    {
        var lockDocument = GpProcessReleaseLock.Parse(CommittedLock);

        var expected = CommittedLock.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        lockDocument.ToIndentedJson().Trim().Should().Be(expected);
    }

    [Fact]
    public void Parse_RejectsUnknownKeysAndWrongSchema()
    {
        var withExtra = CommittedLock.Replace("\"workflowVersion\": \"wf-2026.1-7\"", "\"workflowVersion\": \"wf-2026.1-7\",\n  \"notes\": \"x\"", StringComparison.Ordinal);
        var act = () => GpProcessReleaseLock.Parse(withExtra);
        act.Should().Throw<GpProcessReleaseLockException>().Which.Errors.Should().Contain("notes is not part of the v1 lock");

        var wrongSchema = CommittedLock.Replace("honua.gp-process-release-lock/v1", "honua.gp-process-release-lock/v2", StringComparison.Ordinal);
        var actSchema = () => GpProcessReleaseLock.Parse(wrongSchema);
        actSchema.Should().Throw<GpProcessReleaseLockException>().Which.Errors.Should().Contain("schema must be honua.gp-process-release-lock/v1");
    }

    [Fact]
    public void Validate_NamesEveryOffendingField()
    {
        var lockDocument = GpProcessReleaseLock.Parse(CommittedLock) with
        {
            ConfigurationReferences = [new GpReleaseConfigurationReference { Name = "HONUA_DATA_BUCKET", Reference = "s3://a-real-value" }],
            Targets = [new GpReleaseTarget { Kind = "azure-batch", WorkerImageDigest = "sha256:short" }],
            ValidationReceipts = [],
            GeneratedAt = "2026-10-15 04:00:00",
        };

        var errors = lockDocument.Validate();

        errors.Should().Contain(e => e.Contains("configurationReferences[HONUA_DATA_BUCKET].reference", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("targets[azure-batch].kind", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("targets[azure-batch].workerImageDigest", StringComparison.Ordinal));
        errors.Should().Contain("validation.receipts must hold at least one passed receipt");
        errors.Should().Contain("generatedAt must be yyyy-MM-ddTHH:mm:ssZ");
    }

    [Fact]
    public void Validate_RequiresTheReferenceToEndWithTheDigest()
    {
        var lockDocument = GpProcessReleaseLock.Parse(CommittedLock) with
        {
            RuntimeImage = new GpReleaseRuntimeImage
            {
                Reference = "ghcr.io/honua-io/honua-gp-worker@sha256:0000000000000000000000000000000000000000000000000000000000000000",
                Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            },
        };

        lockDocument.Validate().Should().Contain("runtimeImage.reference must end with runtimeImage.digest");
    }

    [Fact]
    public void FormatGeneratedAt_IsUtcSecondsWithZ()
    {
        var formatted = GpProcessReleaseLock.FormatGeneratedAt(new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.FromHours(-10)));

        formatted.Should().Be("2026-10-16T00:00:00Z");
    }
}
