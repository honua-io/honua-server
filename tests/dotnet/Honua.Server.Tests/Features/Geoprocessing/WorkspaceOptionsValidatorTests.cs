// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geoprocessing;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Tests for workspace options startup validation.
/// </summary>
public class WorkspaceOptionsValidatorTests
{
    private readonly WorkspaceOptionsValidator _validator = new();

    [Fact]
    public void Validate_Defaults_Succeeds()
    {
        var result = _validator.Validate(null, new WorkspaceOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_NegativeCleanupInterval_Fails()
    {
        var options = new WorkspaceOptions { CleanupInterval = TimeSpan.FromMinutes(-1) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CleanupInterval", result.FailureMessage);
    }

    [Fact]
    public void Validate_ZeroCleanupInterval_Fails()
    {
        var options = new WorkspaceOptions { CleanupInterval = TimeSpan.Zero };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CleanupInterval", result.FailureMessage);
    }

    [Fact]
    public void Validate_NegativeCleanupGracePeriod_Fails()
    {
        var options = new WorkspaceOptions { CleanupGracePeriod = TimeSpan.FromMinutes(-1) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CleanupGracePeriod", result.FailureMessage);
    }

    [Fact]
    public void Validate_ZeroCleanupGracePeriod_Succeeds()
    {
        var options = new WorkspaceOptions { CleanupGracePeriod = TimeSpan.Zero };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ZeroMaxCleanupBatchSize_Fails()
    {
        var options = new WorkspaceOptions { MaxCleanupBatchSize = 0 };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxCleanupBatchSize", result.FailureMessage);
    }

    [Fact]
    public void Validate_NegativeMaxCleanupBatchSize_Fails()
    {
        var options = new WorkspaceOptions { MaxCleanupBatchSize = -5 };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxCleanupBatchSize", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveScratchDefaultTtl_Fails(int seconds)
    {
        var options = new WorkspaceOptions { ScratchDefaultTtl = TimeSpan.FromSeconds(seconds) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ScratchDefaultTtl", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveTempLayerDefaultTtl_Fails(int seconds)
    {
        var options = new WorkspaceOptions { TempLayerDefaultTtl = TimeSpan.FromSeconds(seconds) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("TempLayerDefaultTtl", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveResultCollectionDefaultTtl_Fails(int seconds)
    {
        var options = new WorkspaceOptions { ResultCollectionDefaultTtl = TimeSpan.FromSeconds(seconds) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ResultCollectionDefaultTtl", result.FailureMessage);
    }

    [Fact]
    public void Validate_NullTtlOverrides_Succeeds()
    {
        var options = new WorkspaceOptions
        {
            ScratchDefaultTtl = null,
            TempLayerDefaultTtl = null,
            ResultCollectionDefaultTtl = null
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxWorkspaceCount_Fails(int count)
    {
        var options = new WorkspaceOptions { MaxWorkspaceCount = count };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxWorkspaceCount", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxArtifactCount_Fails(int count)
    {
        var options = new WorkspaceOptions { MaxArtifactCount = count };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxArtifactCount", result.FailureMessage);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Validate_NonPositiveMaxStorageBytes_Fails(long bytes)
    {
        var options = new WorkspaceOptions { MaxStorageBytes = bytes };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxStorageBytes", result.FailureMessage);
    }

    [Fact]
    public void Validate_MultipleErrors_ReportsAll()
    {
        var options = new WorkspaceOptions
        {
            CleanupInterval = TimeSpan.Zero,
            MaxCleanupBatchSize = 0,
            ScratchDefaultTtl = TimeSpan.FromSeconds(-1)
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("CleanupInterval", result.FailureMessage);
        Assert.Contains("MaxCleanupBatchSize", result.FailureMessage);
        Assert.Contains("ScratchDefaultTtl", result.FailureMessage);
    }

    [Fact]
    public void Validate_ValidOverrides_Succeeds()
    {
        var options = new WorkspaceOptions
        {
            CleanupInterval = TimeSpan.FromMinutes(5),
            CleanupGracePeriod = TimeSpan.FromMinutes(30),
            MaxCleanupBatchSize = 50,
            ScratchDefaultTtl = TimeSpan.FromHours(2),
            TempLayerDefaultTtl = TimeSpan.FromHours(12),
            ResultCollectionDefaultTtl = TimeSpan.FromDays(7),
            MaxWorkspaceCount = 25,
            MaxArtifactCount = 500,
            MaxStorageBytes = 5L * 1024 * 1024 * 1024
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ScratchTtlExceedsMax_Fails()
    {
        var options = new WorkspaceOptions { ScratchDefaultTtl = TimeSpan.FromHours(48) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ScratchDefaultTtl", result.FailureMessage);
        Assert.Contains("exceeds the maximum", result.FailureMessage);
    }

    [Fact]
    public void Validate_ScratchTtlAtMax_Succeeds()
    {
        var options = new WorkspaceOptions { ScratchDefaultTtl = TimeSpan.FromHours(24) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_TempLayerTtlExceedsMax_Fails()
    {
        var options = new WorkspaceOptions { TempLayerDefaultTtl = TimeSpan.FromDays(14) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("TempLayerDefaultTtl", result.FailureMessage);
        Assert.Contains("exceeds the maximum", result.FailureMessage);
    }

    [Fact]
    public void Validate_TempLayerTtlAtMax_Succeeds()
    {
        var options = new WorkspaceOptions { TempLayerDefaultTtl = TimeSpan.FromDays(7) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ResultCollectionTtlExceedsMax_Fails()
    {
        var options = new WorkspaceOptions { ResultCollectionDefaultTtl = TimeSpan.FromDays(60) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ResultCollectionDefaultTtl", result.FailureMessage);
        Assert.Contains("exceeds the maximum", result.FailureMessage);
    }

    [Fact]
    public void Validate_ResultCollectionTtlAtMax_Succeeds()
    {
        var options = new WorkspaceOptions { ResultCollectionDefaultTtl = TimeSpan.FromDays(30) };

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }
}
