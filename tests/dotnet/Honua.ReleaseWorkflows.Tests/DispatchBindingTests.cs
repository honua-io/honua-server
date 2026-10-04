// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Honua.ReleaseWorkflows.Tests;

public sealed class DispatchBindingTests
{
    private static readonly string[] DispatchArguments = ["--id", "sdk-test", "--repo", "honua-io/client", "--workflow", "conformance.yml", "--ref", "trunk", "--timeout", "2"];
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

    [Theory]
    [InlineData("valid")]
    [InlineData("qualified-path")]
    public async Task Dispatch_ExactImageReceipt_BindsRunAttemptAndDigest(string scenario)
    {
        var result = await DispatchAsync(scenario, "--input", "server_image=" + Image, "--expected-image", Image, "--image-input", "server_image");
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
    [InlineData("wrong-qualified-workflow", "Run identity, source, workflow or attempt changed")]
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

    [Theory]
    [InlineData("merge-train.yml")]
    [InlineData("123456")]
    [InlineData("unknown.yml")]
    public async Task Dispatch_NonReleaseWorkflow_RefusesBeforeGitHub(string workflow)
    {
        var result = await DispatchAsync("valid", "--workflow", workflow);
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal("missing", receipt.RootElement.GetProperty("conclusion").GetString());
        Assert.Equal("Unsupported release workflow: " + workflow, receipt.RootElement.GetProperty("verificationError").GetString());
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

    [Fact]
    public void ReleaseBundle_PullRequestContracts_IsolateDispatchCredentialsAndJobs()
    {
        var workflow = File.ReadAllText(Path.Join(RepositoryRoot, ".github/workflows/release-bundle.yml"));
        var testJob = workflow.Split("  workflow-contract-tests:", StringSplitOptions.None)[1]
            .Split("  release-context:", StringSplitOptions.None)[0];
        Assert.Contains("GH_TOKEN: ''", testJob, StringComparison.Ordinal);
        Assert.Contains("    needs: workflow-contract-tests\n    if: ${{ github.event_name == 'workflow_dispatch' }}", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseRegistry_DispatchableWorkflows_HaveLiteralDispatchDestinations()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Join(RepositoryRoot, "release/bundle-suites.json")));
        var helper = File.ReadAllText(Path.Join(RepositoryRoot, "scripts/release/dispatch-and-wait.sh"));
        var workflows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var suite in registry.RootElement.GetProperty("integration").EnumerateArray())
        {
            if (suite.GetProperty("mode").GetString()!.StartsWith("dispatch", StringComparison.Ordinal) &&
                !suite.GetProperty("refactorPending").GetBoolean())
            {
                workflows.Add(Path.GetFileName(suite.GetProperty("workflow").GetString()!));
            }
        }

        foreach (var suite in registry.RootElement.GetProperty("sdk").EnumerateArray())
        {
            workflows.Add(suite.GetProperty("publishWorkflow").GetString()!);
            if (!suite.GetProperty("refactorPending").GetBoolean() &&
                suite.GetProperty("compatWorkflow").ValueKind == JsonValueKind.String)
            {
                workflows.Add(suite.GetProperty("compatWorkflow").GetString()!);
            }
        }

        Assert.NotEmpty(workflows);
        foreach (var workflow in workflows)
        {
            Assert.Contains("/actions/workflows/" + workflow + "/dispatches", helper, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CiteReceipt_RunningCandidate_ProducesExactDispatchImageReceipt()
    {
        var result = await RunAsync(true, "valid");
        Assert.True(result.ExitCode == 0, result.Error);
        using var receipt = JsonDocument.Parse(result.Receipt);
        Assert.Equal(202, receipt.RootElement.GetProperty("runId").GetInt64());
        Assert.Equal(1, receipt.RootElement.GetProperty("runAttempt").GetInt32());
        Assert.Equal(Image, receipt.RootElement.GetProperty("image").GetString());
        Assert.Equal(Image.Split('@')[1], receipt.RootElement.GetProperty("imageDigest").GetString());
        Assert.Equal("conformance.yml", receipt.RootElement.GetProperty("workflow").GetString());
    }

    [Theory]
    [InlineData("container-missing")]
    [InlineData("container-ambiguous")]
    [InlineData("container-wrong-image")]
    [InlineData("container-stopped")]
    [InlineData("container-wrong-digest")]
    public async Task CiteReceipt_UnprovenRunningImage_DoesNotProduceReceipt(string scenario)
    {
        var result = await RunAsync(true, scenario);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(result.Receipt);
    }

    [Fact]
    public void CiteWorkflow_ReleaseReceipt_FollowsStrictResultPolicyAndRetainsTestContainer()
    {
        var common = File.ReadAllText(Path.Join(RepositoryRoot, ".github/workflows/cite-conformance-common.yml"));
        var caller = File.ReadAllText(Path.Join(RepositoryRoot, ".github/workflows/cite-conformance.yml"));
        Assert.Contains("receipt_args+=(--no-cleanup)", common, StringComparison.Ordinal);
        Assert.True(common.IndexOf("Enforce CITE result policy", StringComparison.Ordinal) <
            common.IndexOf("Write release suite receipt", StringComparison.Ordinal));
        Assert.Contains("if: success() && inputs.release-suite-id != '' && inputs.diagnostic-only != true", common, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", common, StringComparison.Ordinal);
        Assert.Contains("release-compose-file: docker/cite/ogc-api-features/compose.yml", caller, StringComparison.Ordinal);
        Assert.Contains("'server-cite-conformance'", caller, StringComparison.Ordinal);
        Assert.Contains("tested-honua-git-sha: ${{ github.sha }}", caller, StringComparison.Ordinal);
    }

    private static Task<(int ExitCode, string Output, string Error, string Calls, string Receipt)> DispatchAsync(string scenario, params string[] arguments)
        => RunAsync(false, scenario, arguments);

    private static async Task<(int ExitCode, string Output, string Error, string Calls, string Receipt)> RunAsync(bool produceReceipt, string scenario, params string[] arguments)
    {
        var temporary = Path.Join(Path.GetTempPath(), "release-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var fixture = Path.Join(RepositoryRoot, "tests/dotnet/Honua.ReleaseWorkflows.Tests/Fixtures/github.py");
            await File.WriteAllTextAsync(Path.Join(temporary, "gh"), $"#!/bin/bash\nexec python3 '{fixture}' \"$@\"\n");
            await File.WriteAllTextAsync(Path.Join(temporary, "sleep"), "#!/bin/bash\nexit 0\n");
            await File.WriteAllTextAsync(Path.Join(temporary, "docker"), $"#!/bin/bash\nexec python3 '{fixture}' --docker \"$@\"\n");
            if (produceReceipt)
            {
                var workflow = File.ReadAllText(Path.Join(RepositoryRoot, ".github/workflows/cite-conformance-common.yml"));
                var step = workflow.Split("      - name: Write release suite receipt", StringSplitOptions.None)[1]
                    .Split("      - name: Upload release suite receipt", StringSplitOptions.None)[0];
                var script = step.Split("        run: |", StringSplitOptions.None)[1];
                await File.WriteAllTextAsync(Path.Join(temporary, "receipt.sh"), string.Join('\n',
                    script.Split('\n').Where(line => line.StartsWith("          ", StringComparison.Ordinal)).Select(line => line[10..])));
            }
            using (var chmod = Process.Start("chmod", $"+x {temporary}/gh {temporary}/sleep {temporary}/docker"))
            {
                await chmod!.WaitForExitAsync();
                Assert.Equal(0, chmod.ExitCode);
            }

            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = temporary
            };
            start.ArgumentList.Add(produceReceipt ? Path.Join(temporary, "receipt.sh") : Path.Join(RepositoryRoot, "scripts/release/dispatch-and-wait.sh"));
            foreach (var argument in produceReceipt ? arguments : DispatchArguments.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["PATH"] = temporary + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["FIXTURE_ROOT"] = temporary;
            start.Environment["FIXTURE_SCENARIO"] = scenario;
            start.Environment["CANDIDATE_IMAGE"] = Image;
            start.Environment["RELEASE_SUITE_ID"] = "sdk-test";
            start.Environment["CITE_COMPOSE_FILE"] = "compose.yml";
            start.Environment["GITHUB_RUN_ID"] = "202";
            start.Environment["GITHUB_RUN_ATTEMPT"] = "1";
            start.Environment["GITHUB_SHA"] = new string('b', 40);
            start.Environment["GITHUB_REPOSITORY"] = "honua-io/client";
            start.Environment["GITHUB_WORKFLOW_REF"] = "honua-io/client/.github/workflows/conformance.yml@refs/heads/trunk";
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
            var receipt = Path.Join(temporary, "release-suite-receipt.json");
            return (process.ExitCode, await output, await error, File.Exists(calls) ? await File.ReadAllTextAsync(calls) : "",
                File.Exists(receipt) ? await File.ReadAllTextAsync(receipt) : "");
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
