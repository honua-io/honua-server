// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Honua.ReleaseWorkflows.Tests;

public sealed class DispatchBindingTests
{
    private static readonly string[] DispatchArguments = ["--id", "sdk-test", "--repo", "honua-io/client", "--workflow", "compat.yml", "--ref", "trunk", "--timeout", "2"];
    private const string Image = "ghcr.io/honua-io/server@sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Join(directory.FullName, "scripts/release/dispatch-and-wait.sh")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    [Fact]
    public async Task Dispatch_OverlappingUnrelatedSuccess_AwaitsIntendedFailure()
    {
        var result = await DispatchAsync("overlap");
        Assert.Equal(0, result.ExitCode);
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal("failure", receipt.RootElement.GetProperty("conclusion").GetString());
        Assert.Contains("202", result.Calls, StringComparison.Ordinal);
        Assert.DoesNotContain("watch 303", result.Calls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_ExactImageReceipt_BindsRunAttemptAndDigest()
    {
        var result = await DispatchAsync("valid", "--input", "server_image=" + Image, "--expected-image", Image, "--image-input", "server_image");
        Assert.Equal(0, result.ExitCode);
        using var receipt = JsonDocument.Parse(result.Output);
        var root = receipt.RootElement;
        Assert.True(root.GetProperty("conclusion").GetString() == "success", result.Error);
        Assert.Equal(202, root.GetProperty("runId").GetInt64());
        Assert.Equal(1, root.GetProperty("runAttempt").GetInt32());
        Assert.Equal(404, root.GetProperty("receiptArtifactId").GetInt64());
        Assert.Equal(Image, root.GetProperty("image").GetString());
        Assert.Equal(Image.Split('@')[1], root.GetProperty("imageDigest").GetString());
        Assert.EndsWith("/runs/202/attempts/1", root.GetProperty("runUrl").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("run list", result.Calls, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-dispatch-id", "Dispatch did not return one exact run id")]
    [InlineData("ambiguous-dispatch", "Dispatch did not return one exact run id")]
    [InlineData("wrong-event", "Run identity, source, workflow or attempt changed")]
    [InlineData("wrong-sha", "Run identity, source, workflow or attempt changed")]
    [InlineData("wrong-workflow", "Run identity, source, workflow or attempt changed")]
    [InlineData("wrong-repo", "Run identity, source, workflow or attempt changed")]
    [InlineData("wrong-run", "Run identity, source, workflow or attempt changed")]
    [InlineData("rerun", "Run identity, source, workflow or attempt changed")]
    [InlineData("rerun-after-receipt", "Run changed while collecting the suite receipt")]
    [InlineData("timeout", "Dispatched run timed out")]
    [InlineData("missing-receipt", "Suite receipt is missing, expired or not unique")]
    [InlineData("ambiguous-receipt", "Suite receipt is missing, expired or not unique")]
    [InlineData("expired-receipt", "Suite receipt is missing, expired or not unique")]
    [InlineData("receipt-wrong-digest", "Suite receipt does not match the dispatched run, attempt and candidate image")]
    [InlineData("receipt-wrong-image", "Suite receipt does not match the dispatched run, attempt and candidate image")]
    [InlineData("receipt-wrong-run", "Suite receipt does not match the dispatched run, attempt and candidate image")]
    [InlineData("receipt-wrong-attempt", "Suite receipt does not match the dispatched run, attempt and candidate image")]
    [InlineData("receipt-wrong-suite", "Suite receipt does not match the dispatched run, attempt and candidate image")]
    [InlineData("receipt-wrong-sha", "Suite receipt does not match the dispatched run, attempt and candidate image")]
    public async Task Dispatch_UnverifiableSuite_RefusesSuccess(string scenario, string expectedError)
    {
        var result = await DispatchAsync(scenario, "--input", "server_image=" + Image, "--expected-image", Image, "--image-input", "server_image");
        Assert.Equal(0, result.ExitCode);
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal("missing", receipt.RootElement.GetProperty("conclusion").GetString());
        Assert.Equal(expectedError, receipt.RootElement.GetProperty("verificationError").GetString());
        Assert.DoesNotContain("run list", result.Calls, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ghcr.io/honua-io/server:latest", "ghcr.io/honua-io/server:latest")]
    [InlineData(Image, "ghcr.io/honua-io/server:latest")]
    public async Task Dispatch_UnpinnedOrDifferentImage_RefusesBeforeDispatch(string expected, string input)
    {
        var result = await DispatchAsync("valid", "--input", "server_image=" + input, "--expected-image", expected, "--image-input", "server_image");
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal("missing", receipt.RootElement.GetProperty("conclusion").GetString());
        Assert.Empty(result.Calls);
    }

    [Fact]
    public async Task Dispatch_DuplicateImageInput_RefusesBeforeDispatch()
    {
        var result = await DispatchAsync("valid", "--input", "server_image=" + Image, "--input", "server_image=other", "--expected-image", Image, "--image-input", "server_image");
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal("missing", receipt.RootElement.GetProperty("conclusion").GetString());
        Assert.Empty(result.Calls);
    }

    [Fact]
    public async Task Dispatch_DryRun_EmitsSkippedWithoutGitHub()
    {
        var result = await DispatchAsync("valid", "--dry-run");
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal("skipped", receipt.RootElement.GetProperty("conclusion").GetString());
        Assert.Empty(result.Calls);
    }

    [Fact]
    public void ReleaseBundle_ImageSuiteDispatches_RequireExactImageVerification()
    {
        var workflow = File.ReadAllText(Path.Join(RepositoryRoot, ".github/workflows/release-bundle.yml"));
        // Both integration and SDK compatibility callers must opt into the receipt
        // contract; package dry-run dispatches do not supply release suite evidence.
        Assert.Equal(2, workflow.Split("--expected-image \"$image\" --image-input \"$input\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("image=\"${ref}@${digest}\"", workflow, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error, string Calls)> DispatchAsync(string scenario, params string[] arguments)
    {
        var temporary = Path.Join(Path.GetTempPath(), "release-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var fixture = Path.Join(RepositoryRoot, "tests/dotnet/Honua.ReleaseWorkflows.Tests/Fixtures/github.py");
            await File.WriteAllTextAsync(Path.Join(temporary, "gh"), $"#!/bin/bash\nexec python3 '{fixture}' \"$@\"\n");
            await File.WriteAllTextAsync(Path.Join(temporary, "sleep"), "#!/bin/bash\nexit 0\n");
            using (var chmod = Process.Start("chmod", $"+x {temporary}/gh {temporary}/sleep"))
            {
                await chmod!.WaitForExitAsync();
                Assert.Equal(0, chmod.ExitCode);
            }

            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = RepositoryRoot
            };
            start.ArgumentList.Add("scripts/release/dispatch-and-wait.sh");
            foreach (var argument in DispatchArguments.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["PATH"] = temporary + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["FIXTURE_ROOT"] = temporary;
            start.Environment["FIXTURE_SCENARIO"] = scenario;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var calls = Path.Join(temporary, "calls");
            return (process.ExitCode, await output, await error, File.Exists(calls) ? await File.ReadAllTextAsync(calls) : "");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
