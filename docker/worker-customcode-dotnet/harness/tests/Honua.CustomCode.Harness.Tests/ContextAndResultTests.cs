// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.CustomCode.Harness;
using Honua.CustomCode.Sdk;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class ContextAndResultTests
{
    [Fact]
    public void GpResult_Succeeded_IsOk()
    {
        var r = GpResult.Succeeded("done");
        r.Ok.Should().BeTrue();
        r.Status.Should().Be(GpStatus.Succeeded);
        r.Message.Should().Be("done");
    }

    [Fact]
    public void GpResult_Failed_RequiresMessage()
    {
        GpResult.Failed("boom").Ok.Should().BeFalse();
        var act = () => GpResult.Failed("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OutputSink_AddArtifact_StagesFile_AndEnforcesCap()
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-sink");
        try
        {
            // Path.Combine args below are a temp-dir root plus a literal relative file
            // name; no rooted-segment risk (cs/path-combine false positive).
            var path = Path.Join(dir.FullName, "out.txt");
            File.WriteAllText(path, new string('x', 100));

            var sink = new OutputSink(maxTotalBytes: 150);
            var artifact = sink.AddArtifact("out.txt", path);

            artifact.Name.Should().Be("out.txt");
            artifact.SizeBytes.Should().Be(100);
            sink.TotalBytes.Should().Be(100);
            sink.Artifacts.Should().ContainSingle();

            // A second 100-byte file would push past the 150-byte cap.
            var path2 = Path.Join(dir.FullName, "out2.txt");
            File.WriteAllText(path2, new string('y', 100));
            var act = () => sink.AddArtifact("out2.txt", path2);
            act.Should().Throw<OutputSizeExceededException>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/name")]
    [InlineData(".")]
    public void OutputSink_RejectsNonSimpleNames(string name)
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-sink");
        try
        {
            // Path.Combine args are a temp-dir root plus a literal relative file name;
            // no rooted-segment risk (cs/path-combine false positive).
            var path = Path.Join(dir.FullName, "f.txt");
            File.WriteAllText(path, "x");
            var sink = new OutputSink(1000);
            var act = () => sink.AddArtifact(name, path);
            act.Should().Throw<ArgumentException>();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void OutputSink_MissingFile_Throws()
    {
        var sink = new OutputSink(1000);
        var act = () => sink.AddArtifact("missing.txt", "/no/such/file.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ArtifactUploader_UploadsEachArtifact_UnderPrefix()
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-up");
        try
        {
            // Path.Combine args are a temp-dir root plus a literal relative file name;
            // no rooted-segment risk (cs/path-combine false positive).
            var path = Path.Join(dir.FullName, "a.txt");
            File.WriteAllText(path, "hello");
            var puts = new List<(string Bucket, string Key, string Path)>();

            var uploader = new ArtifactUploader("s3://my-bucket/outputs/job-1", (b, k, p) => puts.Add((b, k, p)));
            var results = uploader.Upload([new Artifact("a.txt", path, 5)]);

            puts.Should().ContainSingle();
            puts[0].Bucket.Should().Be("my-bucket");
            puts[0].Key.Should().Be("outputs/job-1/a.txt");
            results.Should().ContainSingle()
                .Which.Uri.Should().Be("s3://my-bucket/outputs/job-1/a.txt");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
