// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Fail-closed admission for <c>conversion.raster-format</c> output drivers (#3307).
/// A target whose GDAL driver is in the effective <c>SkipDrivers</c> set must be
/// rejected before <c>gdal_translate</c> runs, using the same set
/// <see cref="GdalRuntimeHardening"/> writes to <c>GDAL_SKIP</c>.
/// </summary>
public sealed class GdalRasterOutputDriverAdmissionTests
{
    [UnitTest]
    public void TryAdmit_AcceptedDriver_AllowsTargetAbsentFromEffectiveSkipSet()
    {
        var hardening = new GdalHardeningOptions();

        var admitted = GdalRasterOutputDriverAdmission.TryAdmit("PNG", hardening, out var target, out var error);

        admitted.Should().BeTrue();
        error.Should().BeEmpty();
        target.Driver.Should().Be("PNG");
        GdalRuntimeHardening.EffectiveSkipDrivers(hardening).Should().NotContain("PNG");
        GdalRuntimeHardening.BuildEnvironment(hardening, inputReferencesRemoteVsi: false)["GDAL_SKIP"]
            .Split(' ')
            .Should().NotContain("PNG");
    }

    [UnitTest]
    public void TryAdmit_DisabledDriver_NamesDriverAndSkipDriversSetting()
    {
        // Padding and a blank entry must follow the same trim/drop rules as GDAL_SKIP.
        var hardening = new GdalHardeningOptions
        {
            SkipDrivers = ["  ", " GTiff "],
        };

        var admitted = GdalRasterOutputDriverAdmission.TryAdmit("TIFF", hardening, out _, out var error);

        admitted.Should().BeFalse();
        error.Should().Contain("GTiff");
        error.Should().Contain($"{GdalHardeningOptions.SectionName}:SkipDrivers");
        var skip = string.Join(' ', GdalRuntimeHardening.EffectiveSkipDrivers(hardening));
        skip.Should().Be("GTiff");
        skip.Should().Be(GdalRuntimeHardening.BuildEnvironment(hardening, inputReferencesRemoteVsi: false)["GDAL_SKIP"]);
    }

    [UnitTest]
    public void TryAdmit_UnknownDriver_IsRejectedWithoutBlamingSkipDrivers()
    {
        var admitted = GdalRasterOutputDriverAdmission.TryAdmit(
            "BOGUS", new GdalHardeningOptions(), out _, out var error);

        admitted.Should().BeFalse();
        error.Should().Contain("BOGUS");
        error.Should().Contain("not in the allowed set");
        error.Should().NotContain("SkipDrivers");
    }

    [UnitTest]
    public async Task ExecuteAsync_TargetDriverDisabledBySkipDrivers_FailsBeforeGdal()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("must-not-run"));
        var scratch = GdalCli.NewScratch("honua-gdal-skip-driver-admission");
        var hardening = new DivergentHardeningOptions(new GdalHardeningOptions { SkipDrivers = ["PNG"] });
        var executor = new GdalRasterFormatConvertJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalRasterFormatConvertJobExecutor>.Instance,
            hardening);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterFormatConvertJobExecutor.HandledProcessId,
                ("source", GdalCli.RasterBase64("fake-input-raster")),
                ("targetFormat", "PNG"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Failed);
            result.ErrorMessage.Should().Contain("PNG");
            result.ErrorMessage.Should().Contain($"{GdalHardeningOptions.SectionName}:SkipDrivers");
            runner.Invocations.Should().BeEmpty("a skipped output driver is an admission failure, not a GDAL error");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    [UnitTest]
    public async Task ExecuteAsync_ConfiguredSkipDrivers_UsesTheBoundHardeningOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GdalWorker:Hardening:SkipDrivers:0"] = "PNG",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddGdalProcessExecutors(configuration);
        using var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<GdalRasterFormatConvertJobExecutor>();
        var job = GdalJobFactory.Job(
            GdalRasterFormatConvertJobExecutor.HandledProcessId,
            ("source", GdalCli.RasterBase64("fake-input-raster")),
            ("targetFormat", "png"));

        var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("PNG");
        result.ErrorMessage.Should().Contain($"{GdalHardeningOptions.SectionName}:SkipDrivers");
    }

    [UnitTest]
    public async Task ExecuteAsync_ReloadedSkipDrivers_UsesFixedOptionsValueNotMonitorCurrentValue()
    {
        // Runners read IOptions.Value once. A later IOptionsMonitor.CurrentValue that
        // skips PNG must not change admission, or the job and GDAL_SKIP disagree.
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("converted"));
        var scratch = GdalCli.NewScratch("honua-gdal-skip-driver-lifetime");
        var snapshot = new GdalHardeningOptions { SkipDrivers = ["VRT"] };
        var reloaded = new GdalHardeningOptions { SkipDrivers = ["PNG"] };
        var hardening = new DivergentHardeningOptions(snapshot, reloaded);
        hardening.Value.SkipDrivers.Should().NotContain("PNG");
        hardening.CurrentValue.SkipDrivers.Should().Contain("PNG");
        var executor = new GdalRasterFormatConvertJobExecutor(
            runner,
            GdalJobFactory.Options(scratch),
            NullLogger<GdalRasterFormatConvertJobExecutor>.Instance,
            hardening);
        try
        {
            var job = GdalJobFactory.Job(
                GdalRasterFormatConvertJobExecutor.HandledProcessId,
                ("source", GdalCli.RasterBase64("fake-input-raster")),
                ("targetFormat", "PNG"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            (result.ErrorMessage ?? string.Empty).Should().NotContain("SkipDrivers");
            runner.Invocations.Should().ContainSingle();
            runner.Invocations.Single().Arguments.Should().ContainInOrder("-of", "PNG");
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }

    /// <summary>
    /// <c>IOptions.Value</c> is the runner lifetime. <c>IOptionsMonitor.CurrentValue</c>
    /// is a reload the runners ignore. Implementing both lets this fixture still compile
    /// if admission is switched back to the monitor.
    /// </summary>
    private sealed class DivergentHardeningOptions(
        GdalHardeningOptions snapshot,
        GdalHardeningOptions? reloaded = null) : IOptions<GdalHardeningOptions>, IOptionsMonitor<GdalHardeningOptions>
    {
        public GdalHardeningOptions Value { get; } = snapshot;

        public GdalHardeningOptions CurrentValue { get; } = reloaded ?? snapshot;

        public GdalHardeningOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<GdalHardeningOptions, string?> listener) => null;
    }
}
