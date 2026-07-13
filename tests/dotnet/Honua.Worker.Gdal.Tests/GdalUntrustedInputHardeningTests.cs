// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Proves the #2765 GDAL untrusted-input controls actually block: the content
/// pre-check refuses a base64-supplied VRT / XML indirection blob and any
/// <c>/vsi</c> reference before GDAL ever opens it, the base64 input readers wire
/// that pre-check in, and every GDAL subprocess inherits a driver-skip /
/// remote-VSI-disable environment. GDAL is not required for any of these — they
/// pin the pre-check and the subprocess-environment construction (the two controls
/// that block, independent of a live gdal binary). The end-to-end "gdal actually
/// refuses to open the VRT" assertion needs a gdal-present integration run.
/// </summary>
public sealed class GdalUntrustedInputHardeningTests
{
    private const string ScratchSuite = "honua-gdal-hardening-test";

    // A GDAL VRT whose SourceFilename points at a local secret — the file-read vector.
    private const string LocalPathVrt =
        """
        <VRTDataset rasterXSize="512" rasterYSize="512">
          <VRTRasterBand dataType="Byte" band="1">
            <SimpleSource>
              <SourceFilename relativeToVRT="0">/etc/passwd</SourceFilename>
            </SimpleSource>
          </VRTRasterBand>
        </VRTDataset>
        """;

    // A GDAL VRT whose SourceFilename points at a /vsicurl URL — the SSRF vector.
    private const string VsiCurlVrt =
        """
        <VRTDataset rasterXSize="512" rasterYSize="512">
          <VRTRasterBand dataType="Byte" band="1">
            <SimpleSource>
              <SourceFilename relativeToVRT="0">/vsicurl/http://169.254.169.254/latest/meta-data/</SourceFilename>
            </SimpleSource>
          </VRTRasterBand>
        </VRTDataset>
        """;

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static Dictionary<string, string> SourceParam(string base64Value)
        => new(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepInputPrefix + "source"] = base64Value,
        };

    // ---- Pre-check: GdalUntrustedInputGuard ------------------------------------

    [UnitTest]
    public void Guard_RejectsVrtWithLocalSourceFilename()
    {
        var ok = GdalUntrustedInputGuard.IsAdmissible(Encoding.UTF8.GetBytes(LocalPathVrt), out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("XML");
    }

    [UnitTest]
    public void Guard_RejectsVrtWithVsiCurlSourceFilename()
    {
        // XML lead is caught first; either way it must be refused.
        var ok = GdalUntrustedInputGuard.IsAdmissible(Encoding.UTF8.GetBytes(VsiCurlVrt), out var reason);

        ok.Should().BeFalse();
        reason.Should().NotBeEmpty();
    }

    [UnitTest]
    public void Guard_RejectsXmlWithLeadingWhitespaceAndBom()
    {
        var withBomAndIndent = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("\n   " + LocalPathVrt))
            .ToArray();

        var ok = GdalUntrustedInputGuard.IsAdmissible(withBomAndIndent, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("XML");
    }

    [UnitTest]
    public void Guard_RejectsBareVsiReferenceInNonXmlContent()
    {
        // A non-XML text blob that still smuggles a /vsis3 path is refused by the
        // virtual-filesystem scan even though it is not XML.
        var blob = Encoding.UTF8.GetBytes("some,csv,header\n/vsis3/attacker-bucket/secret.tif,1,2\n");

        var ok = GdalUntrustedInputGuard.IsAdmissible(blob, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("/vsis3");
    }

    [UnitTest]
    public void Guard_AdmitsGeoTiffMagicBytes()
    {
        // Little-endian TIFF signature "II*\0" followed by arbitrary binary. A real
        // GeoTIFF must sail through the guard untouched (regression).
        var tiff = new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };

        var ok = GdalUntrustedInputGuard.IsAdmissible(tiff, out var reason);

        ok.Should().BeTrue();
        reason.Should().BeEmpty();
    }

    [UnitTest]
    public void Guard_AdmitsPngMagicBytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

        GdalUntrustedInputGuard.IsAdmissible(png, out _).Should().BeTrue();
    }

    [UnitTest]
    public void Guard_AdmitsGeoJson()
    {
        var geojson = Encoding.UTF8.GetBytes(
            "{\"type\":\"FeatureCollection\",\"features\":[]}");

        GdalUntrustedInputGuard.IsAdmissible(geojson, out _).Should().BeTrue();
    }

    [UnitTest]
    public void Guard_AdmitsKml()
    {
        // KML is XML but a legitimate source.ogr import format — it must NOT be
        // refused by the indirection sniff (only VRT/WMS/WMTS/WCS are). Regression
        // for the narrowing from a blanket XML refusal.
        var kml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <Placemark><Point><coordinates>-105,40,0</coordinates></Point></Placemark>
            </kml>
            """);

        GdalUntrustedInputGuard.IsAdmissible(kml, out var reason).Should().BeTrue(reason);
    }

    [UnitTest]
    public void Guard_AdmitsGml()
    {
        var gml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <ogr:FeatureCollection xmlns:ogr="http://ogr.maptools.org/" xmlns:gml="http://www.opengis.net/gml">
              <gml:featureMember><ogr:layer><ogr:geometryProperty></ogr:geometryProperty></ogr:layer></gml:featureMember>
            </ogr:FeatureCollection>
            """);

        GdalUntrustedInputGuard.IsAdmissible(gml, out var reason).Should().BeTrue(reason);
    }

    [UnitTest]
    public void Guard_RejectsKmlSmugglingVsiReference()
    {
        // An otherwise-admissible KML that smuggles a /vsicurl network link is still
        // refused by the virtual-filesystem scan.
        var hostileKml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <NetworkLink><Link><href>/vsicurl/http://169.254.169.254/latest/meta-data/</href></Link></NetworkLink>
            </kml>
            """);

        var ok = GdalUntrustedInputGuard.IsAdmissible(hostileKml, out var reason);

        ok.Should().BeFalse();
        reason.Should().Contain("/vsicurl");
    }

    [UnitTest]
    public void Guard_RejectsWmsServiceDescription()
    {
        var wms = Encoding.UTF8.GetBytes(
            """
            <GDAL_WMS>
              <Service name="WMS"><ServerUrl>http://169.254.169.254/</ServerUrl></Service>
            </GDAL_WMS>
            """);

        GdalUntrustedInputGuard.IsAdmissible(wms, out var reason).Should().BeFalse();
        reason.Should().Contain("GDAL_WMS");
    }

    [UnitTest]
    public void TryGetBase64Input_AdmitsBase64Kml()
    {
        // The shared decoder must let a base64 KML through so GdalVectorSourceReadJobExecutor
        // can still materialize the .kml and run ogr2ogr.
        var kml = "<?xml version=\"1.0\"?><kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document/></kml>";

        var ok = GdalJobInputReader.TryGetBase64Input(
            SourceParam(Base64(kml)),
            "source",
            maxBytes: 1024 * 1024,
            out var bytes,
            out var error);

        ok.Should().BeTrue(error);
        bytes.Should().NotBeEmpty();
    }

    // ---- Pre-check is wired into the base64 input readers -----------------------

    [UnitTest]
    public void TryGetBase64Input_RejectsBase64Vrt_WithLocalPath()
    {
        var ok = GdalJobInputReader.TryGetBase64Input(
            SourceParam(Base64(LocalPathVrt)),
            "source",
            maxBytes: 1024 * 1024,
            out var bytes,
            out var error);

        ok.Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Contain("rejected");
        error.Should().Contain("source");
    }

    [UnitTest]
    public void TryGetBase64Input_RejectsBase64Vrt_WithVsiCurl()
    {
        var ok = GdalJobInputReader.TryGetBase64Input(
            SourceParam(Base64(VsiCurlVrt)),
            "source",
            out var bytes,
            out var error);

        ok.Should().BeFalse();
        bytes.Should().BeEmpty();
        error.Should().Contain("rejected");
    }

    [UnitTest]
    public void TryDecodeSources_RejectsVrtSourceEntry()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GdalWorkerParameterKeys.StepInputPrefix + "sources"] = Base64(LocalPathVrt),
        };

        var ok = GdalCalcInputs.TryDecodeSources(parameters, 1024 * 1024, out var sources, out var failure);

        ok.Should().BeFalse();
        sources.Should().BeEmpty();
        failure.Should().Contain("rejected");
    }

    // ---- Subprocess environment: GdalRuntimeHardening --------------------------

    [UnitTest]
    public void Hardening_LocalFileInvocation_SkipsVrtAndDisablesRemoteVsi()
    {
        var env = GdalRuntimeHardening.BuildEnvironment(
            new GdalHardeningOptions(),
            inputReferencesRemoteVsi: false);

        env.Should().ContainKey("GDAL_SKIP");
        env["GDAL_SKIP"].Should().Contain("VRT");
        env["GDAL_SKIP"].Should().Contain("OGR_VRT");
        env["GDAL_SKIP"].Should().Contain("WMS");
        env["GDAL_SKIP"].Should().Contain("HTTP");

        // Remote VSI neutralized for a pure local-scratch op.
        env.Should().ContainKey("CPL_VSIL_CURL_ALLOWED_EXTENSIONS");
        env["CPL_VSIL_CURL_ALLOWED_EXTENSIONS"].Should().NotBeEmpty();
        env["GDAL_HTTP_MAX_RETRY"].Should().Be("0");
        env.Should().ContainKey("GDAL_DISABLE_READDIR_ON_OPEN");
        env["GDAL_HTTP_UNSAFESSL"].Should().Be("NO");
    }

    [UnitTest]
    public void Hardening_TrustedVsiInvocation_KeepsRemoteVsiButStillSkipsVrt()
    {
        // The trusted cloud-coverage reader passes a /vsis3 path in argv; it must keep
        // remote VSI working (bucket-scoped range reads) while still refusing VRT.
        var env = GdalRuntimeHardening.BuildEnvironment(
            new GdalHardeningOptions(),
            inputReferencesRemoteVsi: true);

        env["GDAL_SKIP"].Should().Contain("VRT");
        env.Should().NotContainKey("CPL_VSIL_CURL_ALLOWED_EXTENSIONS");
        env.Should().NotContainKey("GDAL_DISABLE_READDIR_ON_OPEN");
    }

    [UnitTest]
    public void Hardening_ArgumentsReferenceVsi_DetectsVsiPaths()
    {
        GdalRuntimeHardening.ArgumentsReferenceVsi(new[] { "-json", "/vsis3/bucket/x.nc" })
            .Should().BeTrue();
        GdalRuntimeHardening.ArgumentsReferenceVsi(new[] { "-json", "/scratch/op/input.tif" })
            .Should().BeFalse();
    }

    [UnitTest]
    public void Hardening_DisableRemoteVsiFalse_LeavesCurlEnabledEvenForLocalOps()
    {
        var env = GdalRuntimeHardening.BuildEnvironment(
            new GdalHardeningOptions { DisableRemoteVsiForLocalInputs = false },
            inputReferencesRemoteVsi: false);

        env["GDAL_SKIP"].Should().Contain("VRT");
        env.Should().NotContainKey("CPL_VSIL_CURL_ALLOWED_EXTENSIONS");
    }

    // ---- Runners actually apply the environment --------------------------------

    [UnitTest]
    public void DockerRunner_LocalFileOp_EmitsHardeningEnvArgs()
    {
        var env = GdalRuntimeHardening.BuildEnvironment(new GdalHardeningOptions(), inputReferencesRemoteVsi: false);
        var args = DockerGdalCommandRunner.BuildDockerRunArguments(
            new GdalContainerExecutionOptions(),
            "gdalinfo",
            new[] { "-json", "/scratch/op/input.tif" },
            "/scratch/op",
            env);

        // Each hardening var appears as a `-e KEY=VALUE` pair before the image.
        args.Should().Contain("-e");
        args.Should().Contain(a => a.StartsWith("GDAL_SKIP=", StringComparison.Ordinal));
        args.Should().Contain(a => a.StartsWith("CPL_VSIL_CURL_ALLOWED_EXTENSIONS=", StringComparison.Ordinal));

        var imageIndex = args.ToList().IndexOf(GdalContainerExecutionOptions.DefaultImage);
        var lastEnvValueIndex = args.ToList().FindLastIndex(a => a.StartsWith("GDAL_", StringComparison.Ordinal));
        lastEnvValueIndex.Should().BeLessThan(imageIndex, "docker flags must precede the image ref");
    }

    [UnitTest]
    public async Task ProcessRunner_AppliesHardeningEnvToSubprocess()
    {
        // Drive a trivial cross-platform command through the runner and have it echo
        // GDAL_SKIP back so we can prove the runner set it on the child environment.
        var runner = new ProcessGdalCommandRunner(
            Options.Create(new GdalHardeningOptions()),
            NullLogger<ProcessGdalCommandRunner>.Instance);

        var (tool, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)new[] { "/c", "echo %GDAL_SKIP%" })
            : ("/bin/sh", new[] { "-c", "printf '%s' \"$GDAL_SKIP\"" });

        var result = await runner.RunAsync(tool, args, Path.GetTempPath(), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.StandardOutput.Should().Contain("VRT");
        result.StandardOutput.Should().Contain("WMS");
    }

    // ---- End-to-end at the executor boundary (no gdal binary needed) -----------

    [UnitTest]
    public async Task StatisticsExecutor_RefusesBase64Vrt_WithoutInvokingGdal()
    {
        // A never-succeeds runner proves the job fails at the input guard, before any
        // gdal invocation — the VRT is refused, not opened.
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdal must not be invoked for a refused VRT input"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterStatisticsJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalRasterStatisticsJobExecutor>.Instance);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterStatisticsJobExecutor.StatisticsProcessId,
                ("source", Base64(LocalPathVrt)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("rejected");
            runner.Invocations.Should().BeEmpty("the VRT must be refused before gdal runs");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task StatisticsExecutor_AcceptsGeoTiff_AndInvokesGdal()
    {
        // Regression: a normal (binary) GeoTIFF passes the guard and reaches gdalinfo,
        // which the fake runner answers with a minimal stats document.
        const string gdalinfoJson =
            "{\"bands\":[{\"band\":1,\"type\":\"Byte\",\"minimum\":0,\"maximum\":255,\"mean\":128,\"stdDev\":10}]}";
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            new GdalCommandResult { ExitCode = 0, StandardOutput = gdalinfoJson });
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterStatisticsJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalRasterStatisticsJobExecutor>.Instance);
        try
        {
            var tiff = new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF };
            var job = GdalJobFactory.Job(
                GdalRasterStatisticsJobExecutor.StatisticsProcessId,
                ("source", Convert.ToBase64String(tiff)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Should().ContainSingle().Which.Tool.Should().Be("gdalinfo");
            context.Artifacts.Should().ContainSingle();
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    // ---- Private-decoder chokepoints: mosaic + vector-reproject (#2776 review) --

    [UnitTest]
    public async Task MosaicExecutor_RefusesBase64Vrt_WithoutInvokingGdal()
    {
        // raster.mosaic decodes its |-separated sources inline (not through the shared
        // GdalJobInputReader chokepoint); prove the guard is applied on that path too.
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdalwarp must not run for a refused VRT source"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterMosaicJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalRasterMosaicJobExecutor>.Instance);
        try
        {
            // Two sources (mosaic requires >= 2); the first is a hostile VRT.
            var sources = Base64(LocalPathVrt) + "|" + Convert.ToBase64String(
                new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 });
            var job = GdalJobFactory.Job(GdalRasterMosaicJobExecutor.HandledProcessId, ("sources", sources));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("rejected");
            runner.Invocations.Should().BeEmpty("the VRT source must be refused before gdalwarp runs");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task VectorReprojectExecutor_RefusesVrtSmuggledAsGeoJsonDataUri_WithoutInvokingGdal()
    {
        // transform.reproject strips a `data:application/geo+json;base64,` prefix and
        // decodes inline; a VRT / service XML smuggled as that "GeoJSON" must be refused.
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("ogr2ogr must not run for a refused indirection blob"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalVectorReprojectJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalVectorReprojectJobExecutor>.Instance);
        try
        {
            var dataUri = "data:application/geo+json;base64," + Base64(LocalPathVrt);
            var job = GdalJobFactory.Job(
                GdalVectorReprojectJobExecutor.HandledProcessId,
                ("fromSrid", "4267"),
                ("toSrid", "4326"),
                ("input", dataUri));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("rejected");
            runner.Invocations.Should().BeEmpty("the indirection blob must be refused before ogr2ogr runs");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task VectorReprojectExecutor_AcceptsGeoJson_AndInvokesGdal()
    {
        // Regression: a genuine GeoJSON data URI passes the guard and reaches ogr2ogr.
        // ogr2ogr arg order is `… <outputPath> <inputPath>`, so the output is the
        // second-to-last argument (index 1 from the end).
        var geojson = "{\"type\":\"FeatureCollection\",\"features\":[]}";
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes(geojson), outputArgIndexFromEnd: 1);
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalVectorReprojectJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalVectorReprojectJobExecutor>.Instance);
        try
        {
            var dataUri = "data:application/geo+json;base64," + Base64(geojson);
            var job = GdalJobFactory.Job(
                GdalVectorReprojectJobExecutor.HandledProcessId,
                ("fromSrid", "4267"),
                ("toSrid", "4326"),
                ("input", dataUri));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Should().ContainSingle().Which.Tool.Should().Be("ogr2ogr");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    // ---- GDALG: caught by GDAL_SKIP, not by the content sniff (#2776 review) ----

    [UnitTest]
    public void GdalgBlob_IsNotCaughtBySniff_ButDriverIsSkipped()
    {
        // A GDALG streamed-algorithm blob is JSON (not XML) and needs no /vsi path, so
        // the content pre-check does NOT catch it — the GDAL_SKIP driver denial is the
        // control. This test pins both facts so the two-layer contract stays honest.
        var gdalg = Encoding.UTF8.GetBytes(
            "{\"type\":\"gdal_streamed_alg\",\"command_line\":\"gdal_translate /etc/hostname out.tif\"}");

        GdalUntrustedInputGuard.IsAdmissible(gdalg, out _).Should().BeTrue(
            "GDALG is JSON with no /vsi reference, so the sniff cannot identify it");

        var env = GdalRuntimeHardening.BuildEnvironment(new GdalHardeningOptions(), inputReferencesRemoteVsi: false);
        env["GDAL_SKIP"].Should().Contain("GDALG", "the GDALG driver must be skipped so the blob cannot open");
        env["GDAL_SKIP"].Should().Contain("MRF");
    }

    // ---- Positive raster-format allowlist (#2784) ------------------------------

    [UnitTest]
    public void BuildEnvironment_NonAllowlistedRasterBombDrivers_AddedToGdalSkip()
    {
        // The JPEG2000 / GIF / BMP / HFA / NITF / ENVI / RMF drivers are outside the
        // dimension-guarded TIFF/PNG/JPEG allowlist and must be denied via GDAL_SKIP so
        // even a misidentified payload cannot open as one (#2784).
        var env = GdalRuntimeHardening.BuildEnvironment(new GdalHardeningOptions(), inputReferencesRemoteVsi: false);

        env["GDAL_SKIP"].Should().Contain("JP2OpenJPEG");
        env["GDAL_SKIP"].Should().Contain("JPEG2000");
        env["GDAL_SKIP"].Should().Contain("GIF");
        env["GDAL_SKIP"].Should().Contain("BMP");
        env["GDAL_SKIP"].Should().Contain("HFA");
        env["GDAL_SKIP"].Should().Contain("NITF");
    }

    [UnitTest]
    public async Task StatisticsExecutor_RefusesNonAllowlistedJp2_WithoutInvokingGdal()
    {
        // A JP2 codestream (FF 4F FF 51) declares dimensions the input guard cannot
        // parse; the positive allowlist must refuse it before gdalinfo opens it (#2784).
        var runner = new FakeGdalCommandRunner((_, _, _) =>
            throw new InvalidOperationException("gdalinfo must not run for a refused JP2 input"));
        var scratch = GdalCli.NewScratch(ScratchSuite);
        var executor = new GdalRasterStatisticsJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalRasterStatisticsJobExecutor>.Instance);
        try
        {
            var jp2 = new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x2F, 0x00, 0x00, 0x00, 0x00 };
            var job = GdalJobFactory.Job(
                GdalRasterStatisticsJobExecutor.StatisticsProcessId,
                ("source", Convert.ToBase64String(jp2)));
            var context = new RecordingJobExecutionContext(job.OperationId);

            var result = await executor.ExecuteAsync(job, context, default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("JPEG2000").And.Contain("allowlist");
            runner.Invocations.Should().BeEmpty("the JP2 input must be refused before gdalinfo runs");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    // ---- KML/GML external-href limitation is documented + pinned (#2776 review) -

    [UnitTest]
    public void Guard_AdmitsKmlWithPlainHttpHref_DocumentedNetworkPostureBoundary()
    {
        // KML/GML are deliberately admitted, and a plain-http <href> (no /vsi) inside a
        // NetworkLink/GroundOverlay is NOT refused by the content guard — it cannot be
        // sniffed apart from a legitimate icon/style href without breaking real KML.
        // External-href fetch safety therefore relies on NETWORK POSTURE (the local-op
        // HTTP-neutralization env, and Container-mode / egress isolation), not this
        // guard. This test pins that boundary so the residual is explicit, not silent.
        var kmlWithHttpHref = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <kml xmlns="http://www.opengis.net/kml/2.2">
              <NetworkLink><Link><href>http://169.254.169.254/latest/meta-data/</href></Link></NetworkLink>
            </kml>
            """);

        GdalUntrustedInputGuard.IsAdmissible(kmlWithHttpHref, out _).Should().BeTrue(
            "plain-http hrefs in admitted KML/GML are gated by network posture, not the content sniff");
    }
}
