// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Import.FileImport;
using Honua.Import.FileImport.Durable;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class ImportObjectStoreCoordinatorTests
{
    private const string RoleArn = "arn:aws:iam::123456789012:role/honua-import";

    [UnitTest]
    public async Task UnsupportedScheme_IsRejectedBeforeAnyRead()
    {
        var reader = new FakeReader();
        var admission = await Evaluate("https://example.com/parcels.geojson", reader, Buckets("example"));

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.UnsupportedScheme);
        admission.Problem.StatusCode.Should().Be(400);
        reader.Heads.Should().Be(0);
        reader.Opens.Should().Be(0);
    }

    [UnitTest]
    public async Task BucketOutsideTheAllowList_IsForbiddenWithoutACloudCall()
    {
        var reader = new FakeReader();
        var admission = await Evaluate("s3://private/parcels.geojson", reader, Buckets("other"));

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.Forbidden);
        admission.Problem.Detail.Should().Contain("private");
        reader.Heads.Should().Be(0);
    }

    [UnitTest]
    public async Task EmptyAllowList_DeniesEveryBucket()
    {
        var reader = new FakeReader();
        var admission = await Evaluate("s3://private/parcels.geojson", reader, Buckets());

        admission.Outcome.Should().Be(ImportObjectStoreAudit.Forbidden);
        reader.Heads.Should().Be(0);
    }

    [UnitTest]
    public async Task BucketPrefix_MatchesOnlyATrailingStar()
    {
        var reader = new FakeReader();
        var allowed = await Evaluate("s3://logs-2026/parcels.geojson", reader, Buckets("logs-*"));
        allowed.Problem.Should().BeNull();

        var literal = await Evaluate("s3://logs-2026/parcels.geojson", new FakeReader(), Buckets("log*s"));
        literal.Problem!.Code.Should().Be(ImportObjectStoreProblems.Forbidden);
    }

    [UnitTest]
    public async Task RoleOutsideTheAllowList_IsForbiddenWithoutACloudCall()
    {
        var reader = new FakeReader();
        var admission = await Evaluate(
            "s3://customer/parcels.geojson",
            reader,
            Buckets("customer"),
            assumeRoleArn: "arn:aws:iam::123456789012:role/other");

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.Forbidden);
        admission.Problem.Detail.Should().Contain("assumeRoleArn");
        reader.Heads.Should().Be(0);
    }

    [UnitTest]
    public async Task ObjectOverTheMax_NamesBothSizesAndDoesNotOpen()
    {
        var reader = new FakeReader { Head = Head(size: 100) };
        var admission = await Evaluate(
            "s3://customer/parcels.geojson",
            reader,
            Buckets("customer"),
            limits: new ImportLimits { MaxObjectStoreImportBytes = 40, BackgroundJobThresholdBytes = 10 });

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.TooLarge);
        admission.Problem.StatusCode.Should().Be(413);
        admission.Problem.Detail.Should().Contain("100").And.Contain("40").And.Contain("MaxObjectStoreImportBytes");
        reader.Heads.Should().Be(1);
        reader.Opens.Should().Be(0);
    }

    [UnitTest]
    public async Task ObjectAboveTheThreshold_QueuesWithoutOpening()
    {
        var reader = new FakeReader { Head = Head(size: 11, etag: "\"pin\"") };
        var admission = await Evaluate(
            "s3://customer/parcels.geojson",
            reader,
            Buckets("customer"),
            limits: new ImportLimits { BackgroundJobThresholdBytes = 10, MaxObjectStoreImportBytes = 1000 });

        admission.Background.Should().BeTrue();
        admission.Request!.ObjectStoreUri.Should().Be("s3://customer/parcels.geojson");
        admission.Request.ObjectStoreETag.Should().Be("\"pin\"");
        admission.Request.FileStream.Should().BeNull();
        reader.Opens.Should().Be(0);
    }

    [UnitTest]
    public async Task ObjectAtTheThreshold_StaysSynchronousAndPinsTheETag()
    {
        var reader = new FakeReader { Head = Head(size: 10, etag: "\"pin\""), Body = "{}"u8.ToArray() };
        var admission = await Evaluate(
            "s3://customer/parcels.geojson",
            reader,
            Buckets("customer"),
            limits: new ImportLimits { BackgroundJobThresholdBytes = 10, MaxObjectStoreImportBytes = 1000 });

        admission.Background.Should().BeFalse();
        admission.DisposeStream.Should().BeTrue();
        admission.Request!.ObjectStoreUri.Should().BeNull();
        reader.OpenedEtag.Should().Be("\"pin\"");
        using var stream = admission.Request.FileStream!;
        var body = await new StreamReader(stream).ReadToEndAsync();
        body.Should().Be("{}");
    }

    [UnitTest]
    public async Task Archive_IsQueuedEvenWhenSmall()
    {
        var reader = new FakeReader { Head = Head(size: 4) };
        var admission = await Evaluate(
            "s3://customer/roads.zip",
            reader,
            Buckets("customer"),
            limits: new ImportLimits { BackgroundJobThresholdBytes = 1000, MaxObjectStoreImportBytes = 5000 },
            formatSupported: name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        admission.Background.Should().BeTrue();
        reader.Opens.Should().Be(0);
    }

    [UnitTest]
    public async Task ChangedETag_IsAConflict()
    {
        var reader = new FakeReader
        {
            Head = Head(size: 4, etag: "\"one\""),
            OpenError = _ => ImportObjectStoreException.Changed("The object changed after the preflight ETag was recorded."),
        };
        var admission = await Evaluate("s3://customer/parcels.geojson", reader, Buckets("customer"));

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.Changed);
        admission.Problem.StatusCode.Should().Be(409);
        admission.Outcome.Should().Be(ImportObjectStoreAudit.Changed);
    }

    [UnitTest]
    public async Task MissingObject_IsNotFound()
    {
        var reader = new FakeReader { HeadError = ImportObjectStoreException.NotFound("The object was not found.") };
        var admission = await Evaluate("s3://customer/missing.geojson", reader, Buckets("customer"));

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.NotFound);
        admission.Problem.StatusCode.Should().Be(404);
        reader.Opens.Should().Be(0);
    }

    [UnitTest]
    public async Task MissingReader_IsUnavailableBeforeACloudCall()
    {
        var admission = await ImportObjectStoreCoordinator.EvaluateAsync(
            Command("s3://customer/parcels.geojson"),
            Buckets("customer"),
            new ImportLimits(),
            reader: null,
            canAcceptBackground: true,
            static name => name.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase),
            CancellationToken.None);

        admission.Problem!.StatusCode.Should().Be(503);
        admission.Problem.Detail.Should().Be(ImportObjectStoreCoordinator.ReaderUnavailableDetail);
        admission.Problem.Code.Should().BeNull();
    }

    [UnitTest]
    public async Task BackgroundUnavailable_UsesTheExistingMessageAndDoesNotOpen()
    {
        var reader = new FakeReader { Head = Head(size: 50) };
        var admission = await Evaluate(
            "s3://customer/parcels.geojson",
            reader,
            Buckets("customer"),
            limits: new ImportLimits { BackgroundJobThresholdBytes = 10, MaxObjectStoreImportBytes = 1000 },
            canAcceptBackground: false);

        admission.Problem!.StatusCode.Should().Be(503);
        admission.Problem.Detail.Should().Be(ImportEndpoints.BackgroundImportUnavailableMessage);
        reader.Opens.Should().Be(0);
        reader.Heads.Should().Be(1);
    }

    [UnitTest]
    public async Task CredentialUri_IsNotCopiedIntoTheAuditSource()
    {
        var reader = new FakeReader();
        var admission = await Evaluate("s3://key:secret@customer/parcels.geojson", reader, Buckets("customer"));

        admission.Problem!.Code.Should().BeNull();
        admission.Problem.Detail.Should().Be("sourceUri must not embed credentials.");
        admission.AuditSourceUri.Should().BeNull();
        admission.AuditRoleArn.Should().BeNull();
        reader.Heads.Should().Be(0);
    }

    [UnitTest]
    public void AuditPayload_OmitsCredentialsAndUsesEtag()
    {
        var json = JsonSerializer.Serialize(
            new ImportObjectStoreAuditPayload
            {
                SourceUri = "s3://customer/parcels.geojson",
                ETag = "\"pin\"",
                SizeBytes = 4,
                AssumeRoleArn = RoleArn,
                Outcome = ImportObjectStoreAudit.Queued,
            },
            ImportJsonContext.Default.ImportObjectStoreAuditPayload);

        json.Should().Contain("\"etag\"");
        json.Should().NotContain("eTag");
        json.Should().NotContain("externalId");
        json.Should().NotContain("session");
    }

    [UnitTest]
    public void JobParameters_RoundTripThePinnedObjectWithoutACloudFile()
    {
        var request = new ImportRequest
        {
            FileName = "parcels.geojson",
            TableName = "parcels",
            SourceKind = "object-store",
            SourceUrl = "s3://customer/parcels.geojson",
            ObjectStoreUri = "s3://customer/parcels.geojson",
            ObjectStoreETag = "\"pin\"",
            ObjectStoreRoleArn = RoleArn,
            ObjectStoreContentType = "application/geo+json",
            TargetSrid = 4326,
        };

        var parameters = FileImportJobParameters.Build(request, 42);
        parameters.Should().NotContainKey(FileImportJobParameters.CloudFileId);
        parameters.Should().NotContainKey("externalId");

        FileImportJobParameters.TryParse(parameters, out var parsed, out var size, out var error).Should().BeTrue(error);
        size.Should().Be(42);
        parsed.Should().BeEquivalentTo(request);
    }

    [UnitTest]
    public void JobParameters_RejectAnObjectStoreJobWithoutAnETag()
    {
        var act = () => FileImportJobParameters.Build(
            new ImportRequest
            {
                FileName = "parcels.geojson",
                TableName = "parcels",
                ObjectStoreUri = "s3://customer/parcels.geojson",
            },
            1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*preflight ETag*");
    }

    private static Task<ImportObjectStoreCoordinator.Admission> Evaluate(
        string sourceUri,
        FakeReader reader,
        ImportObjectStoreOptions options,
        ImportLimits? limits = null,
        string? assumeRoleArn = null,
        bool canAcceptBackground = true,
        Func<string, bool>? formatSupported = null)
        => ImportObjectStoreCoordinator.EvaluateAsync(
            Command(sourceUri, assumeRoleArn),
            options,
            limits ?? new ImportLimits { BackgroundJobThresholdBytes = 1000, MaxObjectStoreImportBytes = 5000 },
            reader,
            canAcceptBackground,
            formatSupported ?? (static name => name.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),
            CancellationToken.None);

    private static ImportObjectStoreCoordinator.Command Command(string sourceUri, string? assumeRoleArn = null)
        => new()
        {
            SourceUri = sourceUri,
            AssumeRoleArn = assumeRoleArn,
            TableName = "parcels",
            TargetSrid = 4326,
        };

    private static ImportObjectStoreOptions Buckets(params string[] buckets)
        => new()
        {
            AllowedBuckets = buckets,
            AssumeRoles = [new ImportObjectStoreRoleOptions { RoleArn = RoleArn, ExternalId = "configured-only" }],
        };

    private static ImportObjectHead Head(long size, string etag = "\"etag\"")
        => new() { SizeBytes = size, ETag = etag, ContentType = "application/geo+json" };

    private sealed class FakeReader : IImportObjectStoreReader
    {
        public int Heads { get; private set; }

        public int Opens { get; private set; }

        public string? OpenedEtag { get; private set; }

        public ImportObjectHead Head { get; init; } = new()
        {
            SizeBytes = 4,
            ETag = "\"etag\"",
            ContentType = "application/geo+json",
        };

        public byte[] Body { get; init; } = Encoding.UTF8.GetBytes("{}");

        public Exception? HeadError { get; init; }

        public Func<string, Exception?>? OpenError { get; init; }

        public Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken)
        {
            Heads++;
            if (HeadError is not null)
            {
                throw HeadError;
            }

            return Task.FromResult(Head);
        }

        public Task<Stream> OpenReadAsync(ImportObjectLocation location, string etag, CancellationToken cancellationToken)
        {
            Opens++;
            OpenedEtag = etag;
            if (OpenError?.Invoke(etag) is Exception error)
            {
                throw error;
            }

            return Task.FromResult<Stream>(new MemoryStream(Body, writable: false));
        }
    }
}
