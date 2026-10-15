// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.ObjectStore;
using Honua.FileStorage;
using Honua.Import.FileImport;
using Microsoft.Extensions.Options;

namespace Honua.Aws.UnitTests;

/// <summary>
/// LocalStack Community coverage for the customer-bucket reader: a small object streams in the
/// request, an object above the background threshold is queued without a body read, and an
/// allow-listed role is assumed with the configured external id.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Emulator")]
[Trait("Tier", "Slow")]
public sealed class ImportObjectStoreLocalStackTests : IClassFixture<ImportObjectStoreLocalStackFixture>
{
    private const string Payload = """{"type":"FeatureCollection","features":[]}""";
    private const string ExternalId = "ext-from-config";

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

        var inner = CreateReader(clients, Buckets(bucket));
        var reader = new CountingReader(inner);
        var admission = await EvaluateAsync($"s3://{bucket}/{key}", reader, Buckets(bucket), SyncLimits());

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

        var inner = CreateReader(clients, Buckets(bucket));
        var reader = new CountingReader(inner);
        var limits = new ImportLimits { BackgroundJobThresholdBytes = 8, MaxObjectStoreImportBytes = 5_000_000 };
        var admission = await EvaluateAsync($"s3://{bucket}/{key}", reader, Buckets(bucket), limits);

        Assert.Null(admission.Problem);
        Assert.True(admission.Background);
        Assert.Equal(1, reader.Heads);
        Assert.Equal(0, reader.Opens);
        Assert.NotNull(admission.Request);
        Assert.Equal($"s3://{bucket}/{key}", admission.Request.ObjectStoreUri);
        Assert.Equal(admission.ETag, admission.Request.ObjectStoreETag);
        Assert.Null(admission.Request.FileStream);

        var location = Location(bucket, key);
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

        var reader = new CountingReader(CreateReader(clients, Buckets(bucket)));
        var limits = new ImportLimits { BackgroundJobThresholdBytes = 8, MaxObjectStoreImportBytes = 8 };
        var admission = await EvaluateAsync($"s3://{bucket}/{key}", reader, Buckets(bucket), limits);

        Assert.Equal(0, reader.Opens);
        Assert.Equal(1, reader.Heads);
        Assert.NotNull(admission.Problem);
        Assert.Equal(ImportObjectStoreProblems.TooLarge, admission.Problem.Code);
        Assert.Equal(413, admission.Problem.StatusCode);
        Assert.Contains(admission.SizeBytes!.Value.ToString(CultureInfo.InvariantCulture), admission.Problem.Detail, StringComparison.Ordinal);
        Assert.Contains(limits.MaxObjectStoreImportBytes.ToString(CultureInfo.InvariantCulture), admission.Problem.Detail, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task BucketOutsideTheAllowList_IsForbiddenBeforeHead()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        const string key = "layers/secret.geojson";
        using var clients = _fixture.CreateClients();
        await PutAsync(clients.S3, bucket, key, Payload);

        var reader = new CountingReader(CreateReader(clients, Buckets("other-bucket")));
        var admission = await EvaluateAsync($"s3://{bucket}/{key}", reader, Buckets("other-bucket"), SyncLimits());

        Assert.Equal(0, reader.Heads);
        Assert.Equal(0, reader.Opens);
        Assert.NotNull(admission.Problem);
        Assert.Equal(ImportObjectStoreProblems.Forbidden, admission.Problem.Code);
    }

    [SkippableFact]
    public async Task MissingObject_IsNotFound_AndAReplacedObject_IsChanged()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        const string key = "layers/replaced.geojson";
        using var clients = _fixture.CreateClients();
        var reader = CreateReader(clients, Buckets(bucket));
        var missing = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => reader.HeadAsync(Location(bucket, key), CancellationToken.None));
        Assert.Equal(ImportObjectStoreProblems.NotFound, missing.Code);

        await PutAsync(clients.S3, bucket, key, Payload);
        var head = await reader.HeadAsync(Location(bucket, key), CancellationToken.None);
        await PutAsync(clients.S3, bucket, key, Payload + " ");
        var changed = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => reader.OpenReadAsync(Location(bucket, key), head.ETag, CancellationToken.None));
        Assert.Equal(ImportObjectStoreProblems.Changed, changed.Code);
    }

    [SkippableFact]
    public async Task AllowListedRole_ReadsWithTheConfiguredExternalId()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        const string key = "layers/assumed.geojson";
        using var clients = _fixture.CreateClients();
        await PutAsync(clients.S3, bucket, key, Payload);
        var roleArn = await CreateExternalIdRoleAsync(clients.Iam, bucket);
        Assert.True(
            await TryAssumeAsync(clients.Sts, roleArn, ExternalId),
            "AssumeRole with the configured external id must succeed against the emulator.");

        var admitted = CreateReader(clients, RoleOptions(bucket, roleArn, ExternalId));
        var head = await admitted.HeadAsync(Location(bucket, key, roleArn), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(head.ETag));
    }

    [SkippableFact]
    public async Task RoleThatRequiresAnExternalId_IsForbiddenWithoutTheConfiguredId()
    {
        Skip.IfNot(_fixture.Available, "LocalStack Community not available (Docker daemon absent).");
        var bucket = NewBucket();
        using var clients = _fixture.CreateClients();
        var roleArn = await CreateExternalIdRoleAsync(clients.Iam, bucket);
        var enforced = !await TryAssumeAsync(clients.Sts, roleArn, externalId: null);
        Skip.If(
            !enforced,
            "LocalStack Community 3.6 did not enforce sts:ExternalId on the role trust policy.");

        var denied = CreateReader(clients, RoleOptions(bucket, roleArn, externalId: null));
        var forbidden = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => denied.HeadAsync(Location(bucket, "layers/assumed.geojson", roleArn), CancellationToken.None));
        Assert.Equal(ImportObjectStoreProblems.Forbidden, forbidden.Code);
        Assert.DoesNotContain(ExternalId, forbidden.Message, StringComparison.Ordinal);
    }

    private static async Task<ImportObjectStoreCoordinator.Admission> EvaluateAsync(
        string sourceUri,
        IImportObjectStoreReader reader,
        ImportObjectStoreOptions options,
        ImportLimits limits)
        => await ImportObjectStoreCoordinator.EvaluateAsync(
            new ImportObjectStoreCoordinator.Command
            {
                SourceUri = sourceUri,
                TableName = "parcels",
                TargetSrid = 4326,
            },
            options,
            limits,
            reader,
            canAcceptBackground: true,
            static name => name.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase),
            CancellationToken.None);

    private static AwsImportObjectStoreReader CreateReader(EmulatorClients clients, ImportObjectStoreOptions options)
        => new(clients.S3, clients.Sts, new StaticOptions(options));

    private static ImportObjectStoreOptions Buckets(params string[] buckets)
        => new() { AllowedBuckets = buckets };

    private static ImportObjectStoreOptions RoleOptions(string bucket, string roleArn, string? externalId)
        => new()
        {
            AllowedBuckets = [bucket],
            AssumeRoles = [new ImportObjectStoreRoleOptions { RoleArn = roleArn, ExternalId = externalId }],
        };

    private static ImportLimits SyncLimits()
        => new() { BackgroundJobThresholdBytes = 1_000_000, MaxObjectStoreImportBytes = 5_000_000 };

    private static ImportObjectLocation Location(string bucket, string key, string? roleArn = null)
        => new()
        {
            Bucket = bucket,
            Key = key,
            AssumeRoleArn = roleArn,
            SourceUri = $"s3://{bucket}/{key}",
        };

    private static string NewBucket() => "honua-imp-" + Guid.NewGuid().ToString("N");

    private static async Task PutAsync(AmazonS3Client s3, string bucket, string key, string body)
    {
        try
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = body,
            ContentType = "application/geo+json",
        });
    }

    private static async Task<string> CreateExternalIdRoleAsync(AmazonIdentityManagementServiceClient iam, string bucket)
    {
        var name = "honua-import-" + Guid.NewGuid().ToString("N")[..12];
        var trust = $$"""
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Principal": { "AWS": "arn:aws:iam::000000000000:root" },
                  "Action": "sts:AssumeRole",
                  "Condition": { "StringEquals": { "sts:ExternalId": "{{ExternalId}}" } }
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
            PolicyName = "read-import",
            PolicyDocument = $$"""
                {
                  "Version": "2012-10-17",
                  "Statement": [
                    {
                      "Effect": "Allow",
                      "Action": ["s3:GetObject", "s3:GetObjectAttributes"],
                      "Resource": "arn:aws:s3:::{{bucket}}/*"
                    }
                  ]
                }
                """,
        });
        return created.Role.Arn;
    }

    private static async Task<bool> TryAssumeAsync(AmazonSecurityTokenServiceClient sts, string roleArn, string? externalId)
    {
        try
        {
            var request = new AssumeRoleRequest
            {
                RoleArn = roleArn,
                RoleSessionName = "honua-import-probe",
                DurationSeconds = 900,
            };
            if (!string.IsNullOrWhiteSpace(externalId))
            {
                request.ExternalId = externalId;
            }

            var response = await sts.AssumeRoleAsync(request);
            return !string.IsNullOrWhiteSpace(response.Credentials?.AccessKeyId);
        }
        catch (AmazonServiceException)
        {
            return false;
        }
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

    private sealed class StaticOptions(ImportObjectStoreOptions value) : IOptionsMonitor<ImportObjectStoreOptions>
    {
        public ImportObjectStoreOptions CurrentValue => value;

        public ImportObjectStoreOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ImportObjectStoreOptions, string?> listener) => null;
    }
}

/// <summary>
/// One LocalStack Community container (S3, IAM, and STS) shared by the object-store import tests.
/// Docker absence degrades to <see cref="Available"/> = false so the tests skip.
/// </summary>
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

    public EmulatorClients CreateClients()
    {
        var s3 = new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };
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
