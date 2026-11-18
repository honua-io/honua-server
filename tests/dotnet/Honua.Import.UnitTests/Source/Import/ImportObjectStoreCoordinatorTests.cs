// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Honua.Import.FileImport;
using Honua.Import.FileImport.Durable;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class ImportObjectStoreCoordinatorTests
{
    [UnitTest]
    public async Task ObjectOverTheMax_NamesBothSizesAndDoesNotOpen()
    {
        var reader = new FakeReader { Head = Head(size: 100) };
        var admission = await Evaluate(
            "parcels.geojson",
            HostFor(reader, new ImportLimits { MaxObjectStoreImportBytes = 40, BackgroundJobThresholdBytes = 10 }));

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
            "parcels.geojson",
            HostFor(reader, new ImportLimits { BackgroundJobThresholdBytes = 10, MaxObjectStoreImportBytes = 1000 }));

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
            "parcels.geojson",
            HostFor(reader, new ImportLimits { BackgroundJobThresholdBytes = 10, MaxObjectStoreImportBytes = 1000 }));

        admission.Background.Should().BeFalse();
        admission.DisposeStream.Should().BeTrue();
        admission.Request!.ObjectStoreUri.Should().BeNull();
        reader.OpenedEtag.Should().Be("\"pin\"");
        using var stream = admission.Request.FileStream!;
        using var textReader = new StreamReader(stream, leaveOpen: true);
        var body = await textReader.ReadToEndAsync();
        body.Should().Be("{}");
    }

    [UnitTest]
    public async Task Archive_IsQueuedEvenWhenSmall()
    {
        var reader = new FakeReader { Head = Head(size: 4) };
        var admission = await Evaluate(
            "roads.zip",
            HostFor(reader, new ImportLimits { BackgroundJobThresholdBytes = 1000, MaxObjectStoreImportBytes = 5000 }) with
            {
                FormatSupported = name => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            });

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
        var admission = await Evaluate("parcels.geojson", HostFor(reader));

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.Changed);
        admission.Problem.StatusCode.Should().Be(409);
        admission.Outcome.Should().Be(ImportObjectStoreAudit.Changed);
    }

    [UnitTest]
    public async Task MissingObject_IsNotFound()
    {
        var reader = new FakeReader { HeadError = ImportObjectStoreException.NotFound("The object was not found.") };
        var admission = await Evaluate("missing.geojson", HostFor(reader));

        admission.Problem!.Code.Should().Be(ImportObjectStoreProblems.NotFound);
        admission.Problem.StatusCode.Should().Be(404);
        reader.Opens.Should().Be(0);
    }

    [UnitTest]
    public async Task MissingReader_IsUnavailableBeforeACloudCall()
    {
        var admission = await Evaluate("parcels.geojson", HostFor(new FakeReader()) with { Reader = null });

        admission.Problem!.StatusCode.Should().Be(503);
        admission.Problem.Detail.Should().Be(ImportObjectStoreCoordinator.ReaderUnavailableDetail);
        admission.Problem.Code.Should().BeNull();
    }

    [UnitTest]
    public async Task BackgroundUnavailable_UsesTheExistingMessageAndDoesNotOpen()
    {
        var reader = new FakeReader { Head = Head(size: 50) };
        var admission = await Evaluate(
            "parcels.geojson",
            HostFor(reader, new ImportLimits { BackgroundJobThresholdBytes = 10, MaxObjectStoreImportBytes = 1000 }) with
            {
                CanAcceptBackground = false,
            });

        admission.Problem!.StatusCode.Should().Be(503);
        admission.Problem.Detail.Should().Be(ImportEndpoints.BackgroundImportUnavailableMessage);
        reader.Opens.Should().Be(0);
        reader.Heads.Should().Be(1);
    }

    [UnitTest]
    public async Task Admission_RecordsTheConnectionAndPinsItOnTheJob()
    {
        var reader = new FakeReader { Head = Head(size: 11, etag: "\"pin\"") };
        var admission = await Evaluate(
            "parcels.geojson",
            HostFor(reader, new ImportLimits { BackgroundJobThresholdBytes = 10, MaxObjectStoreImportBytes = 1000 }));

        admission.AuditConnectionId.Should().Be(Connection.Id.ToString());
        admission.AuditSourceUri.Should().Be("s3://customer/parcels.geojson");
        admission.Request!.ObjectStoreConnectionId.Should().Be(Connection.Id.ToString());
        reader.Locations.Should().ContainSingle().Which.Connection.Should().BeSameAs(Connection);
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
                ConnectionId = Connection.Id.ToString(),
                Outcome = ImportObjectStoreAudit.Queued,
            },
            ImportJsonContext.Default.ImportObjectStoreAuditPayload);

        json.Should().Contain("\"etag\"");
        json.Should().NotContain("eTag");
        json.Should().NotContain("externalId");
        json.Should().NotContain("session");
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData(4326)]
    [InlineData(2193)]
    public void JobParameters_RoundTripThePinnedObjectWithoutACloudFile(int? targetSrid)
    {
        var request = new ImportRequest
        {
            FileName = "parcels.geojson",
            TableName = "parcels",
            SourceKind = "object-store",
            SourceUrl = "s3://customer/parcels.geojson",
            ObjectStoreUri = "s3://customer/parcels.geojson",
            ObjectStoreETag = "\"pin\"",
            ObjectStoreConnectionId = Connection.Id.ToString(),
            ObjectStoreContentType = "application/geo+json",
            TargetSrid = targetSrid,
            SourceSrid = 7912,
            DatumTransformationWkid = 108574,
            CoordinateEpoch = 2020.25,
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

    private static readonly ObjectStorageConnection Connection = new()
    {
        Id = Guid.Parse("8d0f5c55-2a3e-4a8b-9e57-0f5c6f3b9a11"),
        Name = "Customer",
        Bucket = "customer",
        Region = "us-east-1",
        RoleArn = "arn:aws:iam::123456789012:role/honua-connection-customer",
        ExternalId = "external-id-from-setup",
    };

    private static Task<ImportObjectStoreCoordinator.Admission> Evaluate(string key, ImportObjectStoreCoordinator.Host host)
        => ImportObjectStoreCoordinator.EvaluateAsync(
            new ImportObjectStoreCoordinator.Command
            {
                Location = new ImportObjectLocation { Connection = Connection, Key = key },
                TableName = "parcels",
                TargetSrid = 4326,
            },
            host,
            CancellationToken.None);

    private static ImportObjectStoreCoordinator.Host HostFor(FakeReader reader, ImportLimits? limits = null)
        => new()
        {
            Limits = limits ?? new ImportLimits { BackgroundJobThresholdBytes = 1000, MaxObjectStoreImportBytes = 5000 },
            Reader = reader,
            CanAcceptBackground = true,
            FormatSupported = static name => name.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
        };

    private static ImportObjectHead Head(long size, string etag = "\"etag\"")
        => new() { SizeBytes = size, ETag = etag, ContentType = "application/geo+json" };

    private sealed class FakeReader : IImportObjectStoreReader
    {
        public int Heads { get; private set; }

        public int Opens { get; private set; }

        public string? OpenedEtag { get; private set; }

        public List<ImportObjectLocation> Locations { get; } = [];

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
            Locations.Add(location);
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
