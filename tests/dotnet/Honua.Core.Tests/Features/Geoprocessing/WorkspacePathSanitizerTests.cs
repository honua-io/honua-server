// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Unit coverage for <see cref="WorkspacePathSanitizer"/>, the single source of
/// truth shared by the GDAL worker's error sanitizer and the console job
/// glass-box HTTP projection.
/// </summary>
public sealed class WorkspacePathSanitizerTests
{
    [Fact]
    public void Sanitize_ReplacesWorkspacePath_WithStablePlaceholder()
    {
        const string workspace = "/tmp/honua-gdal-worker/job-abc-123";
        var stderr = $"ERROR 1: {workspace}/input.tif: No such file or directory";

        var sanitized = WorkspacePathSanitizer.Sanitize(stderr, workspace);

        Assert.DoesNotContain("/tmp/honua-gdal-worker", sanitized);
        Assert.DoesNotContain("job-abc-123", sanitized);
        Assert.Contains("<scratch>/input.tif", sanitized);
        Assert.Contains("No such file or directory", sanitized);
    }

    [Fact]
    public void Sanitize_HandlesEmptyAndBlankText_WithoutThrowing()
    {
        Assert.Equal(string.Empty, WorkspacePathSanitizer.Sanitize("", "/scratch"));
        Assert.Equal(string.Empty, WorkspacePathSanitizer.Sanitize("   \n", "/scratch"));
    }

    [Fact]
    public void Sanitize_PassesThrough_WhenWorkspaceIsAbsent()
    {
        Assert.Equal("ERROR 1: bad input", WorkspacePathSanitizer.Sanitize("ERROR 1: bad input", ""));
    }

    [Fact]
    public void Sanitize_TruncatesVeryLongText_WithEllipsis()
    {
        var stderr = new string('x', 1000);
        var sanitized = WorkspacePathSanitizer.Sanitize(stderr, "/scratch");
        Assert.True(sanitized.Length < stderr.Length);
        Assert.EndsWith("…", sanitized);
    }

    [Fact]
    public void SanitizeForClient_RedactsKnownWorkspaceAndResidualAbsolutePaths()
    {
        const string workspace = "/tmp/honua-gdal-worker/op-42";
        var message = $"gdalwarp {workspace}/input.tif -> /var/lib/honua/scratch/op-42/out.tif (ok)";

        var sanitized = WorkspacePathSanitizer.SanitizeForClient(message, workspace);

        // Known workspace -> <scratch>
        Assert.DoesNotContain("honua-gdal-worker", sanitized);
        Assert.Contains("<scratch>/input.tif", sanitized);
        // Residual absolute path the worker did not pre-sanitize -> <path>
        Assert.DoesNotContain("/var/lib/honua", sanitized);
        Assert.Contains("<path>", sanitized);
        Assert.Contains("gdalwarp", sanitized);
    }

    [Fact]
    public void SanitizeForClient_RedactsAbsolutePath_WhenWorkspaceUnknown()
    {
        const string message = "Reading from /tmp/honua-gdal-worker/op-77/dem.tif failed";

        var sanitized = WorkspacePathSanitizer.SanitizeForClient(message, workspace: null);

        Assert.DoesNotContain("/tmp/honua-gdal-worker", sanitized);
        Assert.DoesNotContain("op-77", sanitized);
        Assert.Contains("<path>", sanitized);
        Assert.Contains("failed", sanitized);
    }

    [Fact]
    public void SanitizeForClient_RedactsWindowsAbsolutePath()
    {
        const string message = @"gdal_translate C:\honua\scratch\op-9\input.tif done";

        var sanitized = WorkspacePathSanitizer.SanitizeForClient(message, workspace: null);

        Assert.DoesNotContain(@"C:\honua", sanitized);
        Assert.DoesNotContain("op-9", sanitized);
        Assert.Contains("<path>", sanitized);
    }

    [Fact]
    public void TruncateForLog_PreservesPaths_OnlyLengthCaps()
    {
        const string message = "ERROR /tmp/honua-gdal-worker/op-1/x.tif missing";

        var truncated = WorkspacePathSanitizer.TruncateForLog(message);

        // Operator log intentionally KEEPS the scratch path for diagnosis.
        Assert.Equal(message, truncated);
    }
}
