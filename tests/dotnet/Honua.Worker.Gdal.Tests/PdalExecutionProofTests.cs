// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Sdk;
using Xunit.Abstractions;

namespace Honua.Worker.Gdal.Tests;

/// <summary>Production PDAL executor, real LAS fixtures, and an independent binary reader.</summary>
[Trait("Category", "PdalExecutionProof")]
public sealed class PdalExecutionProofTests(ITestOutputHelper testOutput) : IDisposable
{
    private readonly string _scratch = Path.Join(AppContext.BaseDirectory, "pdal-proof", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("geographic.las", "EPSG:4326", 4326, false)]
    [InlineData("mercator.las", null, 3857, false)]
    [InlineData("mercator.las", "EPSG:3857", 4979, true)]
    public async Task Translate_RealLas_PreservesPointsAttributesPrecisionAndCrs(string fixture, string? sourceSrs, int srid, bool reproject)
    {
        var image = Environment.GetEnvironmentVariable("HONUA_PDAL_PROOF_IMAGE");
        image.Should().NotBeNullOrWhiteSpace("build docker/worker-gdal/Dockerfile --target native-tools and supply its immutable image ID");
        (image!.StartsWith("sha256:", StringComparison.Ordinal) || image.Contains("@sha256:", StringComparison.Ordinal))
            .Should().BeTrue("the native proof must bind to immutable image bytes");
        testOutput.WriteLine("Native proof image: {0}; fixture: {1}", image, fixture);
        var runner = new DockerGdalCommandRunner(new ProcessDockerCommandInvoker(NullLogger<ProcessDockerCommandInvoker>.Instance),
            Options.Create(new GdalContainerExecutionOptions { Image = image, User = Environment.GetEnvironmentVariable("HONUA_GDAL_PROOF_USER") ?? "1001:1001" }), Options.Create(new GdalHardeningOptions()),
            Options.Create(new AwsS3Options()), Options.Create(new AzureBlobOptions()), NullLogger<DockerGdalCommandRunner>.Instance);
        var input = await File.ReadAllBytesAsync(Path.Join(AppContext.BaseDirectory, "Fixtures", "PointCloudProof", fixture));
        var inputs = new List<(string, string)> { ("source", Convert.ToBase64String(input)) };
        if (sourceSrs is not null)
        {
            inputs.Add(("sourceSrs", sourceSrs));
        }
        var job = GdalJobFactory.Job("pcloud.translate", inputs.ToArray());
        var context = new RecordingJobExecutionContext(job.OperationId);
        var executor = new PdalPointCloudConvertJobExecutor(runner, GdalJobFactory.Options(_scratch), NullLogger<PdalPointCloudConvertJobExecutor>.Instance);
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        context.Artifacts.Should().ContainSingle();
        var output = GdalCli.DecodeDataUri(context.Artifacts[0]);
        Encoding.ASCII.GetString(output, 0, 4).Should().Be("LASF");
        (output[104] & 0x80).Should().Be(0, "the artifact must be uncompressed LAS");
        output[104].Should().Be(3, "retain RGB and GPS time from point format 3");
        output[25].Should().Be(4, "retain the LAS 1.4 header and WKT CRS");
        BitConverter.ToUInt16(output, 4).Should().Be(7, "retain file source metadata");
        var offset = checked((int)BitConverter.ToUInt32(output, 96));
        var recordLength = BitConverter.ToUInt16(output, 105);
        var count = BitConverter.ToUInt64(output, 247);
        count.Should().Be(3);
        output.Length.Should().BeGreaterThanOrEqualTo(offset + 3 * recordLength);
        var scales = Enumerable.Range(0, 3).Select(i => BitConverter.ToDouble(output, 131 + 8 * i)).ToArray();
        var offsets = Enumerable.Range(0, 3).Select(i => BitConverter.ToDouble(output, 155 + 8 * i)).ToArray();
        var expectedScale = reproject || fixture == "geographic.las" ? 1e-7 : .001;
        scales.Should().Equal(expectedScale, expectedScale, .001);
        double[][] expected = fixture == "geographic.las"
            ? [[-155.1234567, 19.7654321, 12.345], [-155.1234999, 19.7654999, -7.125], [-155.1234001, 19.7654001, 1234.5]]
            : [[500000, 0, 12.345], [611319.491, 111325.143, -7.125], [277361.018, -111325.143, 1234.5]];
        if (reproject)
        {
            // Analytical inverse spherical Mercator over declared (quantized) inputs.
            foreach (var point in expected)
            {
                point[0] = point[0] / 6378137 * 180 / Math.PI;
                point[1] = (2 * Math.Atan(Math.Exp(point[1] / 6378137)) - Math.PI / 2) * 180 / Math.PI;
            }
        }
        void AssertPoints(byte[] candidate)
        {
            for (var i = 0; i < 3; i++)
            {
                var point = offset + i * recordLength;
                for (var axis = 0; axis < 3; axis++)
                {
                    (BitConverter.ToInt32(candidate, point + 4 * axis) * scales[axis] + offsets[axis])
                        .Should().BeApproximately(expected[i][axis], scales[axis] / 2 + 1e-9, $"point {i}, ordinate {axis}");
                }
                BitConverter.ToUInt16(candidate, point + 12).Should().Be(new ushort[] { 10, 60000, 123 }[i]);
                candidate[point + 14].Should().Be(new byte[] { 9, 26, 17 }[i]);
                candidate[point + 15].Should().Be(new byte[] { 2, 6, 9 }[i]);
                unchecked((sbyte)candidate[point + 16]).Should().Be(new sbyte[] { -3, 0, 7 }[i]);
                candidate[point + 17].Should().Be((byte)(i + 1));
                BitConverter.ToUInt16(candidate, point + 18).Should().Be((ushort)(40 + i));
                BitConverter.ToDouble(candidate, point + 20).Should().Be(123456.25 + i * .5);
                BitConverter.ToUInt16(candidate, point + 28).Should().Be(new ushort[] { 65535, 1234, 0 }[i]);
                BitConverter.ToUInt16(candidate, point + 30).Should().Be(new ushort[] { 0, 5678, 65535 }[i]);
                BitConverter.ToUInt16(candidate, point + 32).Should().Be(new ushort[] { 123, 9012, 456 }[i]);
            }
            for (var axis = 0; axis < 3; axis++)
            {
                BitConverter.ToDouble(candidate, 179 + 16 * axis).Should()
                    .BeApproximately(expected.Max(p => p[axis]), scales[axis] / 2 + 1e-9);
                BitConverter.ToDouble(candidate, 187 + 16 * axis).Should()
                    .BeApproximately(expected.Min(p => p[axis]), scales[axis] / 2 + 1e-9);
            }
        }
        AssertPoints(output);
        // Change only intensity: the LAS remains well-formed, with valid bounds/CRS.
        var wrong = (byte[])output.Clone();
        BitConverter.GetBytes((ushort)11).CopyTo(wrong, offset + 12);
        Action rejectWrongIntensity = () => AssertPoints(wrong);
        rejectWrongIntensity.Should().Throw<XunitException>();
        var vlr = (int)BitConverter.ToUInt16(output, 94);
        string? wkt = null;
        for (var i = 0; i < BitConverter.ToUInt32(output, 100); i++)
        {
            var length = BitConverter.ToUInt16(output, vlr + 20);
            if (Encoding.ASCII.GetString(output, vlr + 2, 16).TrimEnd('\0') == "LASF_Projection" && BitConverter.ToUInt16(output, vlr + 18) == 2112)
            {
                wkt = Encoding.UTF8.GetString(output, vlr + 54, length).TrimEnd('\0');
            }
            vlr += 54 + length;
        }
        wkt.Should().NotBeNullOrWhiteSpace("CRS must survive artifact publication");
        Directory.CreateDirectory(_scratch);
        await File.WriteAllTextAsync(Path.Join(_scratch, "crs.wkt"), wkt);
        var decodedCrs = await runner.RunAsync("python3",
            ["-c", "from osgeo import osr; s=osr.SpatialReference(); s.ImportFromWkt(open('crs.wkt').read()); print(s.GetAuthorityCode(None))"],
            _scratch, CancellationToken.None);
        decodedCrs.ExitCode.Should().Be(0, decodedCrs.StandardError);
        decodedCrs.StandardOutput.Trim().Should().Be(srid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose() => GdalCli.CleanupScratch(_scratch);
}
