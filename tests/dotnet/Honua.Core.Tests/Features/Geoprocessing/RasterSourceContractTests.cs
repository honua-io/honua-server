// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.AnalysisContent;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Contract tests for reference-based raster sources carried by durable GP jobs.
/// </summary>
public sealed class RasterSourceContractTests
{
    public static TheoryData<RasterSourceDescriptor> DescriptorCases =>
        new()
        {
            Postgis(),
            Cog(),
            Zarr(),
            Staged(),
            Inline([1, 2, 3, 4]),
        };

    [Theory]
    [MemberData(nameof(DescriptorCases))]
    public void Serialize_Descriptor_RoundTripsWithSourceGeneratedContract(RasterSourceDescriptor descriptor)
    {
        var json = RasterSourceJson.Serialize(descriptor);
        var roundTrip = RasterSourceJson.Deserialize(json);

        Assert.Equal(descriptor.GetType(), roundTrip.GetType());
        Assert.Equal(json, RasterSourceJson.Serialize(roundTrip));
        Assert.Contains("\"sourceType\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_SameVersionWithUnknownOptionalProperty_RemainsCompatible()
    {
        var json = RasterSourceJson.Serialize(Cog());
        var extended = json.Insert(json.Length - 1, ",\"futureOptionalHint\":true");

        var descriptor = RasterSourceJson.Deserialize(extended);

        Assert.IsType<ObjectStoreCogRasterSourceDescriptor>(descriptor);
    }

    [Fact]
    public void Deserialize_SourceTypeAfterDescriptorProperties_IsAccepted()
    {
        var document = JsonNode.Parse(RasterSourceJson.Serialize(Cog()))!.AsObject();
        MoveSourceTypeToEnd(document);

        var descriptor = RasterSourceJson.Deserialize(document.ToJsonString());

        Assert.IsType<ObjectStoreCogRasterSourceDescriptor>(descriptor);
    }

    [Fact]
    public void Deserialize_OmittedSourceContractVersion_IsRejected()
    {
        var document = JsonNode.Parse(RasterSourceJson.Serialize(Cog()))!.AsObject();
        Assert.True(document.Remove("sourceContractVersion"));

        Assert.Throws<JsonException>(() => RasterSourceJson.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void Validate_FutureContractVersion_IsRejectedForRollingUpgradeSafety()
    {
        var descriptor = Cog() with
        {
            SourceContractVersion = RasterSourceContract.CurrentVersion + 1,
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.UnsupportedContractVersion);
    }

    [Fact]
    public void Deserialize_UnknownSourceType_IsRejected()
    {
        const string json = """
            {
              "sourceType": "future-provider",
              "sourceContractVersion": 1,
              "version": "v1"
            }
            """;

        Assert.Throws<JsonException>(() => RasterSourceJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_InlinePayloadWithOversizedEscapedToken_IsRejectedBeforeBase64Decode()
    {
        var document = JsonNode.Parse(RasterSourceJson.Serialize(Inline([1, 2, 3, 4])))!.AsObject();
        var maximumEncodedBytes = ((RasterSourceContract.MaximumInlinePayloadBytes + 2) / 3) * 4;
        document["payload"] = new string('A', (maximumEncodedBytes * 6) + 1);

        var exception = Assert.Throws<JsonException>(() =>
            RasterSourceJson.Deserialize(document.ToJsonString()));

        Assert.Contains("contract ceiling", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_InlinePayloadWithLegalJsonEscapes_UsesLogicalBase64Length()
    {
        var payload = Enumerable.Repeat(
            byte.MaxValue,
            RasterSourceContract.MaximumInlinePayloadBytes).ToArray();
        var encoded = Convert.ToBase64String(payload);
        var json = RasterSourceJson.Serialize(Inline(payload));
        var escapedJson = json.Replace(encoded, encoded.Replace("/", "\\/", StringComparison.Ordinal));

        var descriptor = RasterSourceJson.Deserialize(escapedJson);

        var inline = Assert.IsType<InlineRasterSourceDescriptor>(descriptor);
        Assert.Equal(payload, inline.Payload);
    }

    [Fact]
    public void AnalysisContentJsonContext_InlinePayloadAboveDecodedCeiling_IsRejected()
    {
        var package = new AnalysisPackageContent
        {
            Plan = Plan(new Dictionary<string, RasterSourceDescriptor>
            {
                ["source"] = Inline([1, 2, 3, 4]),
            }),
        };
        var document = JsonNode.Parse(
            JsonSerializer.Serialize(package, AnalysisContentJsonContext.Default.AnalysisPackageContent))!;
        document["plan"]!["steps"]![0]!["rasterSources"]!["source"]!["payload"] =
            Convert.ToBase64String(new byte[RasterSourceContract.MaximumInlinePayloadBytes + 1]);

        var exception = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            document.ToJsonString(), AnalysisContentJsonContext.Default.AnalysisPackageContent));

        Assert.Contains("contract ceiling", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../secret.tif")]
    [InlineData("/vsis3/private-bucket/secret.tif")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("tiles/source.tif?X-Amz-Credential=secret")]
    [InlineData("C:\\secrets\\source.tif")]
    [InlineData("bucket/%2e%2e/secret.tif")]
    [InlineData("bucket/source%00.tif")]
    [InlineData("bucket/source%0a.tif")]
    [InlineData("bucket/source%1f.tif")]
    public void Validate_ObjectStoreKeyInjection_IsRejected(string objectKey)
    {
        var result = RasterSourceDescriptorValidator.Validate(Cog() with { ObjectKey = objectKey });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.UnsafeLocator);
    }

    [Theory]
    [InlineData("artifact%00secret")]
    [InlineData("artifact%0asecret")]
    public void Validate_StagedArtifactEncodedControl_IsRejected(string artifactReference)
    {
        var result = RasterSourceDescriptorValidator.Validate(
            Staged() with { ArtifactReference = artifactReference });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.UnsafeLocator);
    }

    [Theory]
    [InlineData("https://example.com/context")]
    [InlineData("context?access_key=secret")]
    [InlineData("../../context")]
    [InlineData("tenant/context")]
    public void Validate_SecurityContextInjection_IsRejected(string contextReference)
    {
        var descriptor = Cog() with
        {
            SecurityContext = Security() with { AuthorizationSnapshotReference = contextReference },
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InvalidSecurityContext);
    }

    [Fact]
    public void Validate_ObjectStoreReferenceWithoutImmutablePin_IsRejected()
    {
        var descriptor = Cog() with
        {
            Version = string.Empty,
            Content = Content() with { ETag = null, Checksum = null },
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.ImmutableIdentityRequired);
    }

    [Fact]
    public void Validate_InlinePayloadAboveConfiguredCeiling_IsRejected()
    {
        var descriptor = Inline(new byte[17]);
        var options = RasterSourceValidationOptions.Default with { MaxInlineBytes = 16 };

        var result = RasterSourceDescriptorValidator.Validate(descriptor, options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InlinePayloadTooLarge);
    }

    [Fact]
    public void Validate_ConfiguredInlineCeilingCannotExceedWireContractLimit()
    {
        var descriptor = Inline(new byte[RasterSourceContract.MaximumInlinePayloadBytes + 1]);
        var options = RasterSourceValidationOptions.Default with { MaxInlineBytes = int.MaxValue };

        var result = RasterSourceDescriptorValidator.Validate(descriptor, options);

        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InlinePayloadTooLarge);
    }

    [Fact]
    public void Validate_InlinePayloadChecksumMismatch_IsRejected()
    {
        var result = RasterSourceDescriptorValidator.Validate(Inline([1, 2, 3, 4]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.ChecksumMismatch);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("checksumFields")]
    [InlineData("bands")]
    [InlineData("dimensions")]
    public void Validate_ExplicitNestedNulls_ReturnValidationFailuresInsteadOfThrowing(string nullCase)
    {
        var document = JsonNode.Parse(RasterSourceJson.Serialize(Cog()))!.AsObject();
        switch (nullCase)
        {
            case "content":
                document["content"] = null;
                break;
            case "checksumFields":
                document["content"]!["checksum"] = new JsonObject
                {
                    ["algorithm"] = null,
                    ["value"] = null,
                };
                break;
            case "bands":
                document["selection"] = new JsonObject
                {
                    ["bands"] = null,
                    ["dimensions"] = new JsonArray(),
                };
                break;
            case "dimensions":
                document["selection"] = new JsonObject
                {
                    ["bands"] = new JsonArray(),
                    ["dimensions"] = null,
                };
                break;
        }

        var descriptor = RasterSourceJson.Deserialize(document.ToJsonString());
        var exception = Record.Exception(() => RasterSourceDescriptorValidator.Validate(descriptor));

        Assert.Null(exception);
        Assert.False(RasterSourceDescriptorValidator.Validate(descriptor).IsValid);
    }

    [Fact]
    public void ValidatePlan_CountNameAndSerializedBudgets_AreBounded()
    {
        var descriptor = Cog();
        var plan = Plan(new Dictionary<string, RasterSourceDescriptor>
        {
            ["source"] = descriptor,
            ["second"] = descriptor,
        });
        var options = RasterSourceValidationOptions.Default with
        {
            MaxSourcesPerPlan = 1,
            MaxParameterNameLength = 5,
            MaxSerializedBytesPerPlan = 1,
        };

        var result = RasterSourcePlanValidator.Validate(plan, options);

        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.TooManySources);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InvalidParameterName);
        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.SerializedBudgetExceeded);
    }

    [Fact]
    public void ValidatePlan_UnsafeParameterName_IsRejected()
    {
        var result = RasterSourcePlanValidator.Validate(Plan(new Dictionary<string, RasterSourceDescriptor>
        {
            ["source/../../token"] = Cog(),
        }));

        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InvalidParameterName);
    }

    [Fact]
    public void ValidatePlan_DescriptorFailure_ReportsStepBindingAndFieldPath()
    {
        var result = RasterSourcePlanValidator.Validate(Plan(new Dictionary<string, RasterSourceDescriptor>
        {
            ["source"] = Cog(),
            ["mask"] = Cog() with { ObjectKey = "../tenant/mask.tif" },
        }));

        Assert.Contains(result.Errors, error =>
            error.Code == RasterSourceValidationCodes.UnsafeLocator
            && error.Field == "steps[step-0].raster_sources.mask.objectKey");
        Assert.DoesNotContain(result.Errors, error =>
            error.Code == RasterSourceValidationCodes.UnsafeLocator
            && error.Field == "objectKey");
    }

    [Fact]
    public void SecurityContext_CallerCannotMarkReferenceTrusted()
    {
        var document = JsonNode.Parse(RasterSourceJson.Serialize(Cog()))!.AsObject();
        document["securityContext"]!["isTrusted"] = true;

        var descriptor = RasterSourceJson.Deserialize(document.ToJsonString());

        Assert.False(descriptor.SecurityContext.IsTrusted);
    }

    [Fact]
    public void AnalysisContentJsonContext_NestedRasterDescriptor_RoundTrips()
    {
        var package = new AnalysisPackageContent
        {
            Plan = Plan(new Dictionary<string, RasterSourceDescriptor> { ["source"] = Cog() }),
        };

        var json = JsonSerializer.Serialize(package, AnalysisContentJsonContext.Default.AnalysisPackageContent);
        var roundTrip = JsonSerializer.Deserialize(json, AnalysisContentJsonContext.Default.AnalysisPackageContent);

        var source = Assert.Single(Assert.Single(roundTrip!.Plan.Steps).RasterSources).Value;
        Assert.IsType<ObjectStoreCogRasterSourceDescriptor>(source);
    }

    [Fact]
    public void AnalysisContentJsonContext_SourceTypeAfterDescriptorProperties_IsAccepted()
    {
        var package = new AnalysisPackageContent
        {
            Plan = Plan(new Dictionary<string, RasterSourceDescriptor> { ["source"] = Cog() }),
        };
        var document = JsonNode.Parse(
            JsonSerializer.Serialize(package, AnalysisContentJsonContext.Default.AnalysisPackageContent))!;
        var source = document["plan"]!["steps"]![0]!["rasterSources"]!["source"]!.AsObject();
        MoveSourceTypeToEnd(source);

        var roundTrip = JsonSerializer.Deserialize(
            document.ToJsonString(), AnalysisContentJsonContext.Default.AnalysisPackageContent);

        var descriptor = Assert.Single(Assert.Single(roundTrip!.Plan.Steps).RasterSources).Value;
        Assert.IsType<ObjectStoreCogRasterSourceDescriptor>(descriptor);
    }

    [Fact]
    public void AnalysisContentPersistencePolicy_InlineRaster_IsRejected()
    {
        var package = new AnalysisPackageContent
        {
            Plan = Plan(new Dictionary<string, RasterSourceDescriptor> { ["source"] = Inline([1, 2, 3, 4]) }),
        };

        var result = AnalysisContentRasterSourcePolicy.ValidateForPersistence(package);

        Assert.Contains(result.Errors, error => error.Code == RasterSourceValidationCodes.InlinePersistenceDenied);
    }

    [Fact]
    public void Validate_BoundedWindowBandsTimeAndDimensions_AreAccepted()
    {
        var descriptor = Zarr() with
        {
            Selection = new RasterSourceSelection
            {
                PixelWindow = new RasterPixelWindow(10, 20, 256, 256),
                Bands = [1, 3],
                Time = new RasterTimeSelection(
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse("2026-01-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
                Dimensions = [new RasterDimensionSlice("level", 0, 4, 1)],
            },
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    [Fact]
    public void Validate_SelectionCountCeilings_DoNotEnumerateOversizedCollections()
    {
        var descriptor = Zarr() with
        {
            Selection = new RasterSourceSelection
            {
                Bands = new ThrowingReadOnlyList<int>(2),
                Dimensions = new ThrowingReadOnlyList<RasterDimensionSlice>(2),
            },
        };
        var options = RasterSourceValidationOptions.Default with
        {
            MaxBandSelections = 1,
            MaxDimensionSelections = 1,
        };

        var result = RasterSourceDescriptorValidator.Validate(descriptor, options);

        Assert.Equal(2, result.Errors.Count(error =>
            error.Code == RasterSourceValidationCodes.InvalidField
            && error.Message.Contains("configured count limit", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ResolveAsync_MetadataMatchesPinnedDescriptor_ReturnsAvailable()
    {
        var descriptor = Cog();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Available(new RasterSourceMetadata
            {
                Version = descriptor.Version,
                Content = descriptor.Content,
            })));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.Available, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_ChecksumDoesNotMatch_ReturnsIntegrityMismatch()
    {
        var descriptor = Cog();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Available(new RasterSourceMetadata
            {
                Version = descriptor.Version,
                Content = descriptor.Content with
                {
                    Checksum = new RasterChecksum("sha256", new string('b', 64)),
                },
            })));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.IntegrityMismatch, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_MediaTypeDoesNotMatch_ReturnsIntegrityMismatch()
    {
        var descriptor = Cog();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Available(new RasterSourceMetadata
            {
                Version = descriptor.Version,
                Content = descriptor.Content with { MediaType = "application/octet-stream" },
            })));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.IntegrityMismatch, result.Status);
        Assert.Equal("source_integrity_mismatch", result.FailureCode);
    }

    [Fact]
    public async Task ResolveAsync_VersionDoesNotMatch_ReturnsStale()
    {
        var descriptor = Cog();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Available(new RasterSourceMetadata
            {
                Version = "version-2",
                Content = descriptor.Content,
            })));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.Stale, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_MissingArtifact_PreservesMissingStatus()
    {
        var descriptor = Staged();
        var resolver = new StubMetadataResolver((_, _) => Task.FromResult(
            RasterSourceMetadataResolution.Failure(RasterSourceMetadataStatus.Missing, "artifact_missing")));

        var result = await RasterSourceMetadataAdmission.ResolveAsync(descriptor, resolver);

        Assert.Equal(RasterSourceMetadataStatus.Missing, result.Status);
        Assert.Equal("artifact_missing", result.FailureCode);
    }

    [Fact]
    public async Task ResolveAsync_Cancellation_IsPropagatedToResolver()
    {
        var resolver = new StubMetadataResolver(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return RasterSourceMetadataResolution.Failure(RasterSourceMetadataStatus.Missing, "unreachable");
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RasterSourceMetadataAdmission.ResolveAsync(Cog(), resolver, cancellationToken: cancellation.Token));
    }

    private static PostgisRasterSourceDescriptor Postgis() => new()
    {
        Version = "catalog-version-42",
        LayerId = 7,
        RasterId = 123,
        Content = Content(),
        SecurityContext = Security(),
    };

    private static ObjectStoreCogRasterSourceDescriptor Cog() => new()
    {
        Version = "object-version-1",
        StoreReference = "imagery-prod",
        ObjectKey = "tenant-a/imagery/source.tif",
        Content = Content() with { MediaType = "image/tiff", ETag = "etag-1" },
        SecurityContext = Security(),
    };

    private static ObjectStoreZarrRasterSourceDescriptor Zarr() => new()
    {
        Version = "object-version-9",
        StoreReference = "coverage-prod",
        ObjectKey = "tenant-a/climate/model.zarr",
        ArrayPath = "temperature",
        Content = Content() with { MediaType = "application/vnd+zarr", ETag = "etag-zarr" },
        SecurityContext = Security(),
    };

    private static StagedArtifactRasterSourceDescriptor Staged() => new()
    {
        Version = "generation-3",
        ArtifactReference = "artifact-01JZZZZZZZZZZZZZZZZZZZZZZZ",
        Content = Content(),
        SecurityContext = Security(),
    };

    private static InlineRasterSourceDescriptor Inline(byte[] payload) => new()
    {
        Version = "inline-v1",
        Payload = payload,
        Content = Content() with { SizeBytes = payload.Length },
        SecurityContext = Security(),
    };

    private static void MoveSourceTypeToEnd(JsonObject source)
    {
        var sourceType = source["sourceType"]!.GetValue<string>();
        Assert.True(source.Remove("sourceType"));
        source["sourceType"] = sourceType;
    }

    private static AnalysisPlan Plan(IReadOnlyDictionary<string, RasterSourceDescriptor> rasterSources) => new()
    {
        PlanId = "plan-raster-contract",
        IntentId = "intent-raster-contract",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "step-0",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "raster.clip",
                RasterSources = rasterSources,
            },
        ],
    };

    private static RasterContentIdentity Content() => new()
    {
        SizeBytes = 4096,
        MediaType = "application/octet-stream",
        Checksum = new RasterChecksum("sha256", new string('a', 64)),
    };

    private static RasterSecurityContextReference Security() => new()
    {
        TenantId = "tenant-a",
        AuthorizationSnapshotReference = "auth-snapshot-123",
    };

    private sealed class StubMetadataResolver(
        Func<RasterSourceDescriptor, CancellationToken, Task<RasterSourceMetadataResolution>> callback)
        : IRasterSourceMetadataResolver
    {
        public Task<RasterSourceMetadataResolution> ResolveMetadataAsync(
            RasterSourceDescriptor descriptor,
            CancellationToken cancellationToken = default) => callback(descriptor, cancellationToken);
    }

    private sealed class ThrowingReadOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count { get; } = count;

        public T this[int index] => throw new InvalidOperationException("Oversized collection was indexed.");

        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException("Oversized collection was enumerated.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
