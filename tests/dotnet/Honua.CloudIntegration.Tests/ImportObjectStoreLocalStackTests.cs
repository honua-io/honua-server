// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.ObjectStorage;
using Honua.FileStorage;
using Honua.Import.FileImport;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.CloudIntegration.Tests;

[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CloudIntegration)]
[Trait("Category", "Integration")]
[Trait("Category", "Emulator")]
[Trait("Tier", "Slow")]
public sealed class ImportObjectStoreLocalStackTests : IClassFixture<ImportObjectStoreLocalStackFixture>
{
    private const string Payload = """{"type":"FeatureCollection","features":[]}""";

    private readonly ImportObjectStoreLocalStackFixture _fixture;

    public ImportObjectStoreLocalStackTests(ImportObjectStoreLocalStackFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task SmallObject_StreamsInTheRequest_AndPinsTheHeadETag()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        const string key = "layers/parcels.geojson";
        using var clients = _fixture.CreateClients();
        await PutAsync(clients.S3, bucket, key, Payload);

        var reader = new CountingReader(CreateReader(clients));
        var admission = await EvaluateAsync(Location(HostConnection(bucket), key), reader, SyncLimits());

        Assert.Null(admission.Problem);
        Assert.False(admission.Background);
        Assert.Equal(1, reader.Heads);
        Assert.Equal(1, reader.Opens);
        Assert.NotNull(admission.Request);
        Assert.Null(admission.Request.ObjectStoreUri);
        Assert.True(admission.DisposeStream);
        Assert.False(string.IsNullOrWhiteSpace(admission.ETag));
        using var body = admission.Request.FileStream;
        Assert.NotNull(body);
        using var text = new StreamReader(body, Encoding.UTF8);
        Assert.Equal(Payload, await text.ReadToEndAsync());
    }

    [SkippableFact]
    public async Task ObjectAboveTheThreshold_IsQueuedWithoutReadingTheBody()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        const string key = "layers/large.geojson";
        using var clients = _fixture.CreateClients();
        await PutAsync(clients.S3, bucket, key, Payload);

        var inner = CreateReader(clients);
        var reader = new CountingReader(inner);
        var limits = new ImportLimits { BackgroundJobThresholdBytes = 8, MaxObjectStoreImportBytes = 5_000_000 };
        var location = Location(HostConnection(bucket), key);
        var admission = await EvaluateAsync(location, reader, limits);

        Assert.Null(admission.Problem);
        Assert.True(admission.Background);
        Assert.Equal(1, reader.Heads);
        Assert.Equal(0, reader.Opens);
        Assert.NotNull(admission.Request);
        Assert.Equal($"s3://{bucket}/{key}", admission.Request.ObjectStoreUri);
        Assert.Equal(admission.ETag, admission.Request.ObjectStoreETag);
        Assert.Null(admission.Request.FileStream);

        await using var pinned = await inner.OpenReadAsync(location, admission.ETag!, CancellationToken.None);
        using var text = new StreamReader(pinned, Encoding.UTF8);
        Assert.Equal(Payload, await text.ReadToEndAsync());
    }

    [SkippableFact]
    public async Task ObjectOverTheMax_IsTooLargeWithoutABodyRead()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        const string key = "layers/huge.geojson";
        using var clients = _fixture.CreateClients();
        await PutAsync(clients.S3, bucket, key, Payload);

        var reader = new CountingReader(CreateReader(clients));
        var limits = new ImportLimits { BackgroundJobThresholdBytes = 8, MaxObjectStoreImportBytes = 8 };
        var admission = await EvaluateAsync(Location(HostConnection(bucket), key), reader, limits);

        Assert.Equal(0, reader.Opens);
        Assert.Equal(1, reader.Heads);
        Assert.NotNull(admission.Problem);
        Assert.Equal(ImportObjectStoreProblems.TooLarge, admission.Problem.Code);
        Assert.Equal(413, admission.Problem.StatusCode);
        Assert.Contains(admission.SizeBytes!.Value.ToString(CultureInfo.InvariantCulture), admission.Problem.Detail, StringComparison.Ordinal);
        Assert.Contains(limits.MaxObjectStoreImportBytes.ToString(CultureInfo.InvariantCulture), admission.Problem.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task MissingObject_IsNotFound_AndAReplacedObject_IsChanged()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        const string key = "layers/replaced.geojson";
        using var clients = _fixture.CreateClients();
        var reader = CreateReader(clients);
        var location = Location(HostConnection(bucket), key);
        var missing = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => reader.HeadAsync(location, CancellationToken.None));
        Assert.Equal(ImportObjectStoreProblems.NotFound, missing.Code);

        await PutAsync(clients.S3, bucket, key, Payload);
        var head = await reader.HeadAsync(location, CancellationToken.None);
        await PutAsync(clients.S3, bucket, key, Payload + " ");
        var changed = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => reader.OpenReadAsync(location, head.ETag, CancellationToken.None));
        Assert.Equal(ImportObjectStoreProblems.Changed, changed.Code);
    }

    // A customer connection: AssumeRole with the minted external id, then test, browse and import through it.
    [SkippableFact]
    public async Task CustomerConnection_ConnectsBrowsesAndImportsThroughTheAssumedRole()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        using var clients = _fixture.CreateClients();
        await PutAsync(clients.S3, bucket, "parcels/lots.geojson", Payload);
        await PutAsync(clients.S3, bucket, "parcels/2026/later.geojson", Payload);
        await PutAsync(clients.S3, bucket, "private/secret.geojson", Payload);
        var externalId = ObjectStoragePolicy.NewExternalId();
        var connection = HostConnection(bucket) with
        {
            BuiltIn = false,
            Prefix = "parcels/",
            RoleArn = await CreateExternalIdRoleAsync(clients.Iam, bucket, externalId),
            ExternalId = externalId,
        };
        using var factory = CreateFactory(clients);
        var connector = new AwsObjectStoreConnector(factory);

        var health = await connector.TestAsync(connection, CancellationToken.None);
        var page = await connector.BrowseAsync(new ObjectStorageBrowseRequest { Connection = connection, Prefix = "parcels/" }, CancellationToken.None);
        var resolver = new ObjectStorageConnectionResolver(new StaticMonitor(new CloudStorageOptions()), new SingleStore(connection));
        var outside = await resolver.ResolveObjectAsync(connection.Id, "private/secret.geojson", CancellationToken.None);
        var resolved = await resolver.ResolveObjectAsync(connection.Id, "parcels/lots.geojson", CancellationToken.None);
        var admission = await EvaluateAsync(ImportObjectLocation.From(resolved), new AwsImportObjectStoreReader(factory), SyncLimits());

        Assert.True(health.IsHealthy, health.Message);
        Assert.Equal(["parcels/2026/"], page.Folders);
        Assert.Equal("parcels/lots.geojson", Assert.Single(page.Objects).Key);
        Assert.Equal(ObjectStorageResolveFailure.InvalidKey, outside.Failure);
        Assert.Null(admission.Problem);
        Assert.Equal(connection.Id.ToString(), admission.AuditConnectionId);
        using var text = new StreamReader(admission.Request!.FileStream!, Encoding.UTF8);
        Assert.Equal(Payload, await text.ReadToEndAsync());
    }

    private static async Task<ImportObjectStoreCoordinator.Admission> EvaluateAsync(
        ImportObjectLocation location,
        IImportObjectStoreReader reader,
        ImportLimits limits)
        => await ImportObjectStoreCoordinator.EvaluateAsync(
            new ImportObjectStoreCoordinator.Command
            {
                Location = location,
                TableName = "parcels",
                TargetSrid = 4326,
            },
            new ImportObjectStoreCoordinator.Host
            {
                Limits = limits,
                Reader = reader,
                CanAcceptBackground = true,
                FormatSupported = static name => name.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase),
            },
            CancellationToken.None);

    private AwsObjectStoreClientFactory CreateFactory(EmulatorClients clients)
        => new(new AwsObjectStoreClientSeams
        {
            HostClient = clients.S3,
            Sts = clients.Sts,
            SessionClient = credentials => new AmazonS3Client(credentials, _fixture.S3Config()),
            Options = new ImportObjectStoreOptions { ServiceUrl = _fixture.ServiceUrl, Region = "us-east-1" },
        });

    private AwsImportObjectStoreReader CreateReader(EmulatorClients clients) => new(CreateFactory(clients));

    private static ObjectStorageConnection HostConnection(string bucket)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = bucket,
            Bucket = bucket,
            Region = "us-east-1",
            BuiltIn = true,
        };

    private static ImportObjectLocation Location(ObjectStorageConnection connection, string key)
        => new()
        {
            Connection = connection,
            Key = key,
        };

    private static ImportLimits SyncLimits()
        => new() { BackgroundJobThresholdBytes = 1_000_000, MaxObjectStoreImportBytes = 5_000_000 };

    private static string NewBucket() => "honua-imp-" + Guid.NewGuid().ToString("N");

    private static async Task PutAsync(AmazonS3Client s3, string bucket, string key, string body)
    {
        try
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Create is idempotent. The object write below uses the existing bucket.
            _ = ex;
        }

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = body,
            ContentType = "application/geo+json",
        });
    }

    private static async Task<string> CreateExternalIdRoleAsync(AmazonIdentityManagementServiceClient iam, string bucket, string externalId)
    {
        var name = ObjectStorageConnectionKinds.RoleNamePrefix + Guid.NewGuid().ToString("N")[..12];
        var trust = $$"""
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Principal": { "AWS": "arn:aws:iam::000000000000:root" },
                  "Action": "sts:AssumeRole",
                  "Condition": { "StringEquals": { "sts:ExternalId": "{{externalId}}" } }
                }
              ]
            }
            """;
        var created = await iam.CreateRoleAsync(new CreateRoleRequest
        {
            RoleName = name,
            AssumeRolePolicyDocument = trust,
        });
        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = name,
            PolicyName = "read-connection",
            PolicyDocument = $$"""
                {
                  "Version": "2012-10-17",
                  "Statement": [
                    { "Effect": "Allow", "Action": "s3:GetObject", "Resource": "arn:aws:s3:::{{bucket}}/*" },
                    { "Effect": "Allow", "Action": ["s3:ListBucket", "s3:GetBucketLocation"], "Resource": "arn:aws:s3:::{{bucket}}" }
                  ]
                }
                """,
        });
        return created.Role.Arn;
    }

    private sealed class CountingReader(IImportObjectStoreReader inner) : IImportObjectStoreReader
    {
        public int Heads { get; private set; }

        public int Opens { get; private set; }

        public Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken)
        {
            Heads++;
            return inner.HeadAsync(location, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(ImportObjectLocation location, string etag, CancellationToken cancellationToken)
        {
            Opens++;
            return inner.OpenReadAsync(location, etag, cancellationToken);
        }
    }

    private sealed class StaticMonitor(CloudStorageOptions value) : IOptionsMonitor<CloudStorageOptions>
    {
        public CloudStorageOptions CurrentValue => value;

        public CloudStorageOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<CloudStorageOptions, string?> listener) => null;
    }

    private sealed class SingleStore(ObjectStorageConnection connection) : IObjectStorageConnectionStore
    {
        public Task<bool> SaveSetupAsync(ObjectStorageConnectionSetup setup, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ObjectStorageConnectionSetup?> FindOpenSetupAsync(string externalId, CancellationToken cancellationToken)
            => Task.FromResult<ObjectStorageConnectionSetup?>(null);

        public Task<ObjectStorageCreateOutcome> CreateAsync(ObjectStorageConnection item, CancellationToken cancellationToken)
            => Task.FromResult(ObjectStorageCreateOutcome.SetupRejected);

        public Task<ObjectStorageConnection?> GetAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(id == connection.Id ? connection : null);

        public Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObjectStorageConnection>>([connection]);

        public Task<bool> UpdateAsync(ObjectStorageConnection item, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

public sealed class ImportObjectStoreLocalStackFixture : IAsyncLifetime
{
    private const string Image = "localstack/localstack:3.6.0";
    private const int EdgePort = 4566;
    private const string Services = "s3,iam,sts";

    private IContainer? _container;

    public bool Available { get; private set; }

    public string ServiceUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage(Image)
                .WithPortBinding(EdgePort, true)
                .WithEnvironment("SERVICES", Services)
                .WithEnvironment("DEFAULT_REGION", "us-east-1")
                .WithEnvironment("AWS_DEFAULT_REGION", "us-east-1")
                .WithEnvironment("AWS_ACCESS_KEY_ID", "test")
                .WithEnvironment("AWS_SECRET_ACCESS_KEY", "test")
                .Build();
            await _container.StartAsync();
            var port = _container.GetMappedPublicPort(EdgePort);
            ServiceUrl = $"http://127.0.0.1:{port}";
            await WaitForReadyAsync(port);
            Available = true;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            Available = false;
            if (_container is not null)
            {
                await _container.DisposeAsync();
                _container = null;
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    public AmazonS3Config S3Config()
        => new()
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };

    public EmulatorClients CreateClients()
    {
        var s3 = S3Config();
        var sts = new AmazonSecurityTokenServiceConfig
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = "us-east-1",
        };
        var iam = new AmazonIdentityManagementServiceConfig
        {
            ServiceURL = ServiceUrl,
            AuthenticationRegion = "us-east-1",
        };
        return new EmulatorClients(
            new AmazonS3Client("test", "test", s3),
            new AmazonSecurityTokenServiceClient("test", "test", sts),
            new AmazonIdentityManagementServiceClient("test", "test", iam));
    }

    private static async Task WaitForReadyAsync(int port)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/_localstack/health");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (IsUp(body, "s3") && IsUp(body, "iam") && IsUp(body, "sts"))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The emulator is still starting. The loop retries until the deadline.
                _ = ex;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException("Timed out waiting for LocalStack s3, iam, and sts.");
    }

    private static bool IsUp(string body, string service)
        => body.Contains($"\"{service}\": \"available\"", StringComparison.OrdinalIgnoreCase)
           || body.Contains($"\"{service}\":\"available\"", StringComparison.OrdinalIgnoreCase)
           || body.Contains($"\"{service}\": \"running\"", StringComparison.OrdinalIgnoreCase)
           || body.Contains($"\"{service}\":\"running\"", StringComparison.OrdinalIgnoreCase);
}

public sealed class EmulatorClients(
    AmazonS3Client s3,
    AmazonSecurityTokenServiceClient sts,
    AmazonIdentityManagementServiceClient iam) : IDisposable
{
    public AmazonS3Client S3 { get; } = s3;

    public AmazonSecurityTokenServiceClient Sts { get; } = sts;

    public AmazonIdentityManagementServiceClient Iam { get; } = iam;

    public void Dispose()
    {
        S3.Dispose();
        Sts.Dispose();
        Iam.Dispose();
    }
}
