// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalNoDataTests
{
    private const string ScratchSuite = "honua-gdal-nodata-test";

    [UnitTest]
    public void ParseBandNoData_FiniteNumber_ReturnsValue()
    {
        const string json = """{"bands":[{"band":1,"noDataValue":-9999.5}]}""";

        GdalNoData.ParseBandNoData(json).Should().Be(-9999.5d);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("NaN")]
    [InlineData("nan")]
    [InlineData(" NaN ")]
    public void ParseBandNoData_NaNString_ReturnsDoubleNaN(string token)
    {
        var json = "{\"bands\":[{\"band\":1,\"noDataValue\":\"" + token + "\"}]}";

        var parsed = GdalNoData.ParseBandNoData(json);

        parsed.Should().NotBeNull();
        double.IsNaN(parsed!.Value).Should().BeTrue();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("Infinity", true)]
    [InlineData("+Infinity", true)]
    [InlineData("inf", true)]
    [InlineData("+inf", true)]
    [InlineData("-Infinity", false)]
    [InlineData("-inf", false)]
    public void ParseBandNoData_InfinityStrings_ReturnsSignedInfinity(string token, bool positive)
    {
        var json = "{\"bands\":[{\"band\":1,\"noDataValue\":\"" + token + "\"}]}";

        var parsed = GdalNoData.ParseBandNoData(json);

        parsed.Should().NotBeNull();
        if (positive)
        {
            double.IsPositiveInfinity(parsed!.Value).Should().BeTrue();
        }
        else
        {
            double.IsNegativeInfinity(parsed!.Value).Should().BeTrue();
        }
    }

    [UnitTest]
    public void ParseBandNoData_NumericString_ReturnsParsedDouble()
    {
        const string json = """{"bands":[{"band":1,"noDataValue":"-32768"}]}""";

        GdalNoData.ParseBandNoData(json).Should().Be(-32768d);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"bands":[{"band":1,"noDataValue":null}]}""")]
    [InlineData("""{"bands":[{"band":1,"noDataValue":"not-a-number"}]}""")]
    public void ParseBandNoData_MissingOrInvalid_ReturnsNull(string json)
    {
        GdalNoData.ParseBandNoData(json).Should().BeNull();
    }

    [UnitTest]
    public void FormatCalcLiteral_NonFiniteValues_ProducesValidPythonTokens()
    {
        GdalNoData.FormatCalcLiteral(double.NaN, qualifiedNumpy: false).Should().Be("nan");
        GdalNoData.FormatCalcLiteral(double.PositiveInfinity, qualifiedNumpy: false).Should().Be("inf");
        GdalNoData.FormatCalcLiteral(double.NegativeInfinity, qualifiedNumpy: false).Should().Be("-inf");

        GdalNoData.FormatCalcLiteral(double.NaN, qualifiedNumpy: true).Should().Be("numpy.nan");
        GdalNoData.FormatCalcLiteral(double.PositiveInfinity, qualifiedNumpy: true).Should().Be("numpy.inf");
        GdalNoData.FormatCalcLiteral(double.NegativeInfinity, qualifiedNumpy: true).Should().Be("-numpy.inf");
        GdalNoData.FormatCalcLiteral(-9999d, qualifiedNumpy: false).Should().Be("-9999");
    }

    [UnitTest]
    public async Task MapAlgebra_SourceWithStringNaNNoData_PropagatesNaNWithoutFallbackProbe()
    {
        var runner = new FakeGdalCommandRunner((tool, args, _) =>
        {
            if (tool == "gdalinfo")
            {
                return new GdalCommandResult
                {
                    ExitCode = 0,
                    StandardOutput = """{"bands":[{"band":1,"noDataValue":"NaN"}]}""",
                };
            }

            File.WriteAllBytes(args[^1], Encoding.UTF8.GetBytes("calc-tif"));
            return new GdalCommandResult { ExitCode = 0 };
        });

        var scratch = GdalCli.NewScratch(ScratchSuite);
        try
        {
            var executor = new GdalRasterMapAlgebraJobExecutor(
                runner,
                GdalJobFactory.Options(scratch),
                NullLogger<GdalRasterMapAlgebraJobExecutor>.Instance);
            var job = GdalJobFactory.Job(
                GdalRasterMapAlgebraJobExecutor.HandledProcessId,
                ("sources", GdalCli.RasterBase64("float-dem")),
                ("expression", "A*2"));

            var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
            runner.Invocations.Should().NotContain(i => i.Tool == "python3");
            var calcArgs = runner.Invocations.Single(i => i.Tool == "gdal_calc.py").Arguments;
            calcArgs.Should().Contain("--NoDataValue=NaN");
            calcArgs.Should().Contain(a => a.Contains("nan=nan,posinf=nan,neginf=nan", StringComparison.Ordinal));
        }
        finally
        {
            GdalCli.CleanupScratch(scratch);
        }
    }
}
