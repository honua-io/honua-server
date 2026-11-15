// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Server.Features.Geoprocessing.CustomCode;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class CustomCodeArtifactScratchTests
{
    [UnitTest]
    public async Task Download_KeepsVerifiedScratchPrivateUntilResponseDisposesIt()
    {
        byte[] bytes = [1, 2, 3];
        var receipt = CreateReceipt(bytes);
        var job = CreateJob(receipt);
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.GetJobAsync(receipt.JobId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(job);
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(receipt.Provider);
        storage.GetMetadataAsync(receipt.ObjectKey, Arg.Any<CancellationToken>()).Returns(new CloudFile
        {
            FileId = receipt.ObjectKey, FileName = receipt.Name, StoragePath = receipt.ObjectKey,
            ContentType = receipt.ContentType, SizeBytes = bytes.Length,
            UploadedAt = DateTimeOffset.UtcNow, Provider = receipt.Provider
        });
        storage.DownloadAsync(receipt.ObjectKey, Arg.Any<CancellationToken>()).Returns(new MemoryStream(bytes));
        await using var services = new ServiceCollection().AddSingleton(storage).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var handler = typeof(CustomCodeArtifactEndpoints).GetMethod("DownloadAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

        var result = await (Task<IResult>)handler.Invoke(null, [receipt.JobId, receipt.Name, 1, context, jobs])!;

        var response = result.Should().BeOfType<FileStreamHttpResult>().Subject;
        string path;
        await using (var stream = response.FileStream)
        {
            path = stream.Should().BeOfType<FileStream>().Subject.Name;
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(PrivateTempFile.OwnerReadWrite);
            }
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.ToArray().Should().Equal(bytes);
        }
        File.Exists(path).Should().BeFalse();
    }

    private static CustomCodeArtifactDescriptor CreateReceipt(byte[] bytes)
    {
        return new CustomCodeArtifactDescriptor
        {
            JobId = "scratch-job", Attempt = 1, Name = "result.bin",
            ObjectKey = "gp/custom-artifacts/scratch-job/a1/result.bin",
            Provider = CloudStorageProvider.AwsS3, ContentType = "application/octet-stream",
            SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), State = "available"
        };
    }

    private static ExecutionJobRecord CreateJob(CustomCodeArtifactDescriptor receipt)
    {
        return new ExecutionJobRecord
        {
            OperationId = receipt.JobId, Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = [receipt.Serialize()],
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch", WorkloadName = "custom-worker"
            }
        };
    }
}
