// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// The first tests in this repository that execute real PDAL (honua-server#4401).
/// </summary>
/// <remarks>
/// <para>
/// Before this file, <c>pcloud.translate</c> — a GA catalog operation — had no executed receipt
/// at all: its thirteen tests asserted the argument list handed to
/// <c>FakeGdalCommandRunner</c>, whose fixture bytes are string literals. No workflow installed
/// PDAL and the only invocation anywhere in the tree was <c>pdal --version</c>.
/// </para>
/// <para>
/// The oracle here is independently computed, not snapshotted. The test authors a small ASCII
/// point set, compresses it to LAZ with PDAL, runs the production executor over that LAZ, and
/// then parses the emitted LAS public header block and point records <em>by offset, against the
/// LAS 1.x specification</em> — sharing no code with the executor or with the server's
/// <c>LasPointCloudReader</c> — and asserts the decoded coordinates equal the coordinates that
/// went in.
/// </para>
/// </remarks>
public sealed class PdalPointCloudRealCliTests
{
    private const string ScratchSuite = "pdal-real-cli";

    /// <summary>The exact points authored into the source cloud; the decode oracle.</summary>
    private static readonly (double X, double Y, double Z)[] SourcePoints =
    [
        (-122.4194, 37.7749, 12.5),
        (-122.4180, 37.7760, 15.25),
        (-122.4160, 37.7735, 9.75),
        (-122.4205, 37.7710, 21.0),
    ];

    [PdalCliFact]
    [Protocol(ProtocolNames.TestQuality)]
    [Operation(Operations.TestInfrastructure)]
    public async Task PcloudTranslate_WithRealPdal_DecompressesLazToLasPreservingEveryPoint()
    {
        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var lazBytes = await CreateCompressedSourceAsync(scratch).ConfigureAwait(false);

            // LAZ is genuinely compressed: 'LASF' still opens the file, but the point records
            // are laz-perf chunks. If the executor merely copied the input through, the header
            // assertions below would still pass — the per-point decode is what catches that.
            lazBytes.Length.Should().BeGreaterThan(0);

            var executor = new PdalPointCloudConvertJobExecutor(
                new ProcessGdalCommandRunner(
                    Options.Create(new GdalHardeningOptions()),
                    Options.Create(new AwsS3Options()),
                    Options.Create(new AzureBlobOptions()),
                    NullLogger<ProcessGdalCommandRunner>.Instance),
                GdalJobFactory.Options(scratch),
                NullLogger<PdalPointCloudConvertJobExecutor>.Instance);

            var job = GdalJobFactory.Job(
                PdalPointCloudConvertJobExecutor.HandledProcessId,
                ("source", Convert.ToBase64String(lazBytes)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            context.Artifacts.Should().ContainSingle();

            var las = GdalCli.DecodeDataUri(context.Artifacts[0]);
            var decoded = LasReader.Read(las);

            decoded.PointCount.Should().Be(SourcePoints.Length,
                "every source point must survive decompression");
            decoded.IsCompressed.Should().BeFalse(
                "the executor's contract is an UNCOMPRESSED LAS artifact the managed tiler can parse");

            // Order is not part of the contract; match each source point to a decoded one.
            foreach (var (x, y, z) in SourcePoints)
            {
                decoded.Points.Should().Contain(
                    point => Math.Abs(point.X - x) < 1e-6
                        && Math.Abs(point.Y - y) < 1e-6
                        && Math.Abs(point.Z - z) < 1e-6,
                    "point ({0}, {1}, {2}) must round-trip through LAZ -> pdal translate -> LAS", x, y, z);
            }

            // And the header bounds must agree with the points, so a header written from stale
            // metadata cannot pass while the records are right (or vice versa).
            decoded.MinX.Should().BeApproximately(SourcePoints.Min(p => p.X), 1e-6);
            decoded.MaxX.Should().BeApproximately(SourcePoints.Max(p => p.X), 1e-6);
            decoded.MinY.Should().BeApproximately(SourcePoints.Min(p => p.Y), 1e-6);
            decoded.MaxY.Should().BeApproximately(SourcePoints.Max(p => p.Y), 1e-6);

            // honua-server#4401 regression guard. Before the executor forwarded the source's
            // scale, `pdal translate` fell back to its default 0.01 — 0.01 DEGREES here, roughly
            // 1.1 km of horizontal quantisation on a conversion whose entire contract is
            // "decompress verbatim". The coordinate assertions above are what catch it; this
            // pins the mechanism so the cause is obvious when they fail.
            decoded.ScaleX.Should().BeLessThan(
                1e-5,
                "a geographic cloud decompressed with LAS's default 0.01 scale is quantised to ~1 km");
            decoded.ScaleY.Should().BeLessThan(1e-5);
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    /// <summary>
    /// Writes the source points as ASCII and compresses them to LAZ with PDAL, so the input to
    /// the executor is a genuine laz-perf-compressed cloud rather than a fixture string.
    /// </summary>
    private static async Task<byte[]> CreateCompressedSourceAsync(string scratch)
    {
        Directory.CreateDirectory(scratch);

        var textPath = Path.Join(scratch, "source.txt");
        var builder = new StringBuilder("X,Y,Z\n");
        foreach (var (x, y, z) in SourcePoints)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{x},{y},{z}\n");
        }

        await File.WriteAllTextAsync(textPath, builder.ToString()).ConfigureAwait(false);

        var lazPath = Path.Join(scratch, "source.laz");
        await GdalCli.RunPdalAsync(
            [
                "translate",
                textPath,
                lazPath,
                "--writers.las.compression=true",
                "--writers.las.scale_x=0.0000001",
                "--writers.las.scale_y=0.0000001",
                "--writers.las.scale_z=0.001",
            ],
            scratch).ConfigureAwait(false);

        return await File.ReadAllBytesAsync(lazPath).ConfigureAwait(false);
    }

    /// <summary>
    /// A minimal LAS 1.x public-header-block and point-record reader written directly against
    /// the ASPRS specification's byte offsets. Deliberately independent of both the executor and
    /// the server's <c>LasPointCloudReader</c> so "the points survived" is not proven by the same
    /// code that would have lost them.
    /// </summary>
    private static class LasReader
    {
        public static DecodedCloud Read(byte[] las)
        {
            Encoding.ASCII.GetString(las, 0, 4).Should().Be("LASF", "an LAS artifact must carry the LASF signature");

            var pointDataFormat = las[104];
            var pointRecordLength = BinaryPrimitives.ReadUInt16LittleEndian(las.AsSpan(105, 2));
            var offsetToPointData = BinaryPrimitives.ReadUInt32LittleEndian(las.AsSpan(96, 4));
            long pointCount = BinaryPrimitives.ReadUInt32LittleEndian(las.AsSpan(107, 4));

            var scaleX = BitConverter.ToDouble(las, 131);
            var scaleY = BitConverter.ToDouble(las, 139);
            var scaleZ = BitConverter.ToDouble(las, 147);
            var offsetX = BitConverter.ToDouble(las, 155);
            var offsetY = BitConverter.ToDouble(las, 163);
            var offsetZ = BitConverter.ToDouble(las, 171);
            var maxX = BitConverter.ToDouble(las, 179);
            var minX = BitConverter.ToDouble(las, 187);
            var maxY = BitConverter.ToDouble(las, 195);
            var minY = BitConverter.ToDouble(las, 203);

            // LAS 1.4 moves the authoritative count into the extended field; the legacy field is
            // zero for formats above 5. Fall back so the reader works on either version.
            if (pointCount == 0 && las.Length >= 255)
            {
                pointCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(las.AsSpan(247, 8));
            }

            // Bit 7 of the point-data-format byte marks laz-perf compression; the executor's
            // contract is that it is clear.
            var isCompressed = (pointDataFormat & 0x80) != 0;

            var points = new List<(double X, double Y, double Z)>((int)pointCount);
            for (long index = 0; index < pointCount; index++)
            {
                var recordOffset = checked((int)(offsetToPointData + (index * pointRecordLength)));
                var rawX = BinaryPrimitives.ReadInt32LittleEndian(las.AsSpan(recordOffset, 4));
                var rawY = BinaryPrimitives.ReadInt32LittleEndian(las.AsSpan(recordOffset + 4, 4));
                var rawZ = BinaryPrimitives.ReadInt32LittleEndian(las.AsSpan(recordOffset + 8, 4));
                points.Add((
                    (rawX * scaleX) + offsetX,
                    (rawY * scaleY) + offsetY,
                    (rawZ * scaleZ) + offsetZ));
            }

            return new DecodedCloud(pointCount, isCompressed, points, minX, maxX, minY, maxY, scaleX, scaleY);
        }

        public sealed record DecodedCloud(
            long PointCount,
            bool IsCompressed,
            IReadOnlyList<(double X, double Y, double Z)> Points,
            double MinX,
            double MaxX,
            double MinY,
            double MaxY,
            double ScaleX,
            double ScaleY);
    }
}
