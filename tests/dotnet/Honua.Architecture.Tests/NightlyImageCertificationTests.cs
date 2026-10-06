// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the contract between the nightly image and full-matrix certification
/// (honua-io/honua-release#376 R18): the nightly images the newest certified trunk
/// sha, publishes the per-sha tags the release resolver looks up, and a dispatched
/// full matrix runs every certification lane ci.yml declares.
/// </summary>
public sealed partial class NightlyImageCertificationTests
{
    private const string NightlyPath = ".github/workflows/nightly-container-build.yml";
    private const string CiPath = ".github/workflows/ci.yml";
    private const string SelectorPath = "scripts/ci/select-certified-nightly-sha.py";
    private const string CandidateSha = "${{ needs.select-candidate.outputs.sha }}";
    private const string CandidateSha7 = "${{ needs.select-candidate.outputs.sha7 }}";

    // honua-release certification/full-matrix-checks.yaml requires exactly these check-run names.
    private static readonly string[] ResolverLanes =
    [
        "Build & Format Check",
        ".NET Foundation Tests",
        "Python Integration Tests",
        "Test Suite Summary",
        "CI Gate",
    ];

    private static readonly string[] BuildJobs =
    [
        "mirror-base-images",
        "build-aot",
        "build-lambda-aot",
        "verify-functions-aot",
        "build-jit",
    ];

    [ArchitectureTest]
    public void NightlyBuild_ShouldImageTheSelectedCertifiedSha_NeverTheCronHead()
    {
        var nightly = Read(NightlyPath);

        var select = Job(nightly, "select-candidate");
        select.Should().Contain("python3 scripts/ci/select-certified-nightly-sha.test.py",
            "the selector proves its own certification rule before it chooses anything");
        select.Should().Contain("python3 " + SelectorPath);
        select.Should().Contain("--workflow .github/workflows/ci.yml");
        select.Should().Contain("--limit 100", "the walk is bounded like the resolver's");
        select.Should().Contain("--sha \"${REQUESTED_SHA:-}\"");
        select.Should().NotContain("\n    if:", "trunk selection must run on every schedule and dispatch");

        // The only path that hands the build its own head is a non-trunk branch proof,
        // and that path publishes nothing.
        var headPath = select.IndexOf("echo \"sha=${GITHUB_SHA}\"", StringComparison.Ordinal);
        var branchGuard = select.IndexOf("if [ \"${GITHUB_REF}\" != \"refs/heads/trunk\" ]; then", StringComparison.Ordinal);
        branchGuard.Should().BeGreaterThan(-1);
        headPath.Should().BeGreaterThan(branchGuard, "github.sha may only be imaged off trunk");
        select[branchGuard..select.IndexOf("python3 " + SelectorPath, StringComparison.Ordinal)]
            .Should().Contain("exit 0", "the trunk path must fall through to certification, never to HEAD");

        foreach (var job in BuildJobs)
        {
            var block = Job(nightly, job);
            NeedsOf(block).Should().Contain("select-candidate", job);
            block.Should().Contain($"ref: {CandidateSha}", $"{job} must check out the certified candidate");
            block.Should().NotContain("${{ github.sha }}", job);
        }

        nightly.Should().NotContain("type=sha", "metadata-action sha tags name github.sha, not the candidate");
        nightly.Should().NotContain("HONUA_GIT_SHA=${{ github.sha }}");
        nightly.Should().Contain($"HONUA_GIT_SHA={CandidateSha}", Exactly.Times(4),
            "AOT, Lambda AOT, Functions AOT and JIT are all stamped with the candidate sha");
        nightly.Should().Contain($"org.opencontainers.image.revision={CandidateSha}", Exactly.Times(4),
            "the resolver binds a candidate by its config revision label, which metadata-action would otherwise set to github.sha");
        nightly.Should().Contain("uses: actions/checkout@v7", Exactly.Times(6),
            "only the selector itself checks out the workflow revision; every build checks out the candidate");
        nightly.Should().Contain($"ref: {CandidateSha}", Exactly.Times(5));

        foreach (var trunkOnly in new[] { "build-aot", "build-jit", "manifest-aot", "manifest-lambda-aot", "manifest-jit" })
        {
            Job(nightly, trunkOnly).Should().Contain("github.ref == 'refs/heads/trunk'",
                $"{trunkOnly} publishes channel tags, which only a trunk run may move");
        }
    }

    [ArchitectureTest]
    public void NightlyBuild_ShouldPublishTheTagsTheReleaseResolverLooksUp()
    {
        var nightly = Read(NightlyPath);

        // nightly-<sha7> and its nightly-aot-<sha7> compatibility alias.
        Job(nightly, "build-aot").Should().Contain($"type=raw,value=nightly-{CandidateSha7}");
        var aotManifest = Job(nightly, "manifest-aot");
        aotManifest.Should().Contain($"type=raw,value=nightly-{CandidateSha7}");
        aotManifest.Should().Contain("compatibility_tag=\"${repository}:nightly-aot-${tag_name#nightly-}\"");

        // nightly-lambda-aot-<sha7>-<arch>, read by the resolver for amd64.
        var lambda = Job(nightly, "build-lambda-aot");
        lambda.Should().Contain($"type=raw,value=nightly-lambda-aot-{CandidateSha7}");
        lambda.Should().Contain("suffix=-${{ matrix.arch }}");
        lambda.Should().Contain("- arch: amd64");
        lambda.Should().Contain("- arch: arm64");
        Job(nightly, "manifest-lambda-aot").Should().Contain($"type=raw,value=nightly-lambda-aot-{CandidateSha7}");

        // Date and channel tags survive alongside the per-sha tags.
        foreach (var tag in new[]
                 {
                     "type=raw,value=nightly-${{ needs.mirror-base-images.outputs.build_date }}",
                     "type=raw,value=nightly-lambda-aot-${{ needs.mirror-base-images.outputs.build_date }}",
                     "type=raw,value=nightly-jit-${{ needs.mirror-base-images.outputs.build_date }}",
                     "type=raw,value=nightly\n",
                     "type=raw,value=nightly-lambda-aot\n",
                     $"type=raw,value=nightly-jit-{CandidateSha7}",
                     $"type=raw,value=nightly-functions-aot-{CandidateSha7}",
                 })
        {
            nightly.Should().Contain(tag);
        }

        foreach (var staging in new[]
                 {
                     "boundary-candidate-nightly-aot-${{ matrix.arch }}-" + CandidateSha,
                     "boundary-candidate-nightly-lambda-aot-${{ matrix.arch }}-" + CandidateSha,
                     "boundary-candidate-nightly-functions-aot-" + CandidateSha,
                 })
        {
            nightly.Should().Contain(staging);
        }
    }

    [ArchitectureTest]
    public void NightlyBuild_ShouldVerifyTheCandidateWithThisWorkflowRevisionsTooling()
    {
        var nightly = Read(NightlyPath);

        foreach (var job in new[] { "build-aot", "build-lambda-aot", "verify-functions-aot" })
        {
            var block = Job(nightly, job);
            block.Should().Contain("git archive \"${GITHUB_SHA}\" scripts/ci/verify-serving-image-boundary.py scripts/ci/promote-verified-image.py",
                $"{job} calls the verifier with this revision's flags, so it must run this revision's verifier");
            block.Should().Contain("working-directory: ${{ runner.temp }}/nightly-tooling", Exactly.Twice(),
                $"{job} verifies and promotes from the staged tooling");
        }

        Job(nightly, "mirror-base-images").Should().Contain(
            "scripts/ci/base-image-mirrors.sh --verify \"${RUNNER_TEMP}/nightly-container-build.yml\"",
            "the running workflow consumes the mirror tags; the candidate's Dockerfiles produce them");
    }

    [ArchitectureTest]
    public async Task CiWorkflow_ShouldDeclareTheCertificationLanesOnce()
    {
        var ci = Read(CiPath);

        var declaration = LaneDeclaration().Match(ci);
        declaration.Success.Should().BeTrue("ci.yml declares CERTIFICATION_LANE_JOBS in its workflow-level env");
        var names = declaration.Groups["jobs"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(job => JobName(ci, job))
            .ToArray();
        names.Should().Equal(ResolverLanes, "the nightly selector and honua-release's resolver require the same lanes");

        foreach (var consumer in new[] { SelectorPath, NightlyPath })
        {
            var text = Read(consumer);
            foreach (var lane in ResolverLanes)
            {
                text.Should().NotContain($"\"{lane}\"", $"{consumer} must read the lane list from ci.yml, not re-declare it");
            }
        }

        await RunPython("scripts/ci/select-certified-nightly-sha.test.py");
    }

    [ArchitectureTest]
    public void CiWorkflow_CertificationLanesShouldNotBeGatedAwayFromWorkflowDispatch()
    {
        var ci = Read(CiPath);

        foreach (var job in new[] { "build", "dotnet-foundation-tests", "python-integration-tests", "test-all", "ci-gate" })
        {
            var condition = IfOf(Job(ci, job));
            condition.Should().NotContain("github.event_name == 'schedule'", job);
            condition.Should().NotContain("github.event_name == 'pull_request'", job);
            condition.Should().NotContain("github.event_name != 'workflow_dispatch'", job);
        }

        IfOf(Job(ci, "build")).Should().Contain("needs.changes.outputs.build_changes == 'true'");
        IfOf(Job(ci, "dotnet-foundation-tests")).Should().Contain("needs.changes.outputs.integration_changes == 'true'");
        IfOf(Job(ci, "python-integration-tests")).Should().Contain("needs.changes.outputs.full_ci == 'true'");
    }

    [ArchitectureTest]
    public async Task CiChangeDetection_ShouldRunTheFullMatrixWhenTheRouterResolvesRunAll()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // An empty range is the 7aad613 re-dispatch (selective_base == HEAD): the
        // router answers run_all, so every certification lane must run.
        var trailingEmpty = await RunChangeDetection("workflow_dispatch", "HEAD...HEAD", trailingSelective: true);
        trailingEmpty["build_changes"].Should().Be("true");
        trailingEmpty["integration_changes"].Should().Be("true");
        trailingEmpty["full_ci"].Should().Be("true");
        trailingEmpty["postgres_matrix"].Should().Contain("postgis/postgis:18-3.6");

        var scheduled = await RunChangeDetection("schedule", "HEAD...HEAD", trailingSelective: false);
        scheduled["full_ci"].Should().Be("true");

        // A diff the runner cannot compute fails safe to the full lane on a dispatch.
        var unavailable = await RunChangeDetection("workflow_dispatch", "0000000000000000000000000000000000000000...HEAD", trailingSelective: true);
        unavailable["full_ci"].Should().Be("true");
        unavailable["build_changes"].Should().Be("true");

        // Pull requests keep their path-based skipping.
        var pullRequest = await RunChangeDetection("pull_request", "HEAD...HEAD", trailingSelective: false);
        pullRequest["build_changes"].Should().Be("false");
        pullRequest["integration_changes"].Should().Be("false");
        pullRequest["full_ci"].Should().Be("false");
        (await RunChangeDetection("pull_request", "0000000000000000000000000000000000000000...HEAD", trailingSelective: false))["full_ci"]
            .Should().Be("false");
    }

    private static async Task<Dictionary<string, string>> RunChangeDetection(string eventName, string range, bool trailingSelective)
    {
        var ci = Read(CiPath);
        var changes = Job(ci, "changes");
        var start = changes.IndexOf("- name: Check for non-test changes", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var runStart = changes.IndexOf("        run: |\n", start, StringComparison.Ordinal) + "        run: |\n".Length;
        var script = string.Join('\n', changes[runStart..]
            .Split('\n')
            .TakeWhile(line => line.Length == 0 || line.StartsWith("          ", StringComparison.Ordinal))
            .Select(line => line.Length == 0 ? line : line[10..]));

        var expressions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["steps.diff.outputs.range"] = range,
            ["github.event_name"] = eventName,
            ["inputs.full_ci"] = "false",
            ["steps.diff.outputs.trailing_selective"] = trailingSelective ? "true" : "false",
        };
        script = Expression().Replace(script, match =>
        {
            expressions.TryGetValue(match.Groups["expr"].Value, out var value)
                .Should().BeTrue($"the change-detection step uses an expression this test must model: {match.Value}");
            return value!;
        });

        var scratch = Directory.CreateTempSubdirectory("ci-changes-");
        try
        {
            var scriptPath = Path.Join(scratch.FullName, "changes.sh");
            var outputPath = Path.Join(scratch.FullName, "output");
            File.WriteAllText(scriptPath, script);
            File.WriteAllText(outputPath, string.Empty);

            var startInfo = new ProcessStartInfo("bash")
            {
                WorkingDirectory = ArchitectureTestHelpers.ResolveRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.Environment["GITHUB_OUTPUT"] = outputPath;
            startInfo.Environment["GITHUB_REF_NAME"] = "trunk";
            startInfo.Environment["PR_LABELS_JSON"] = "[]";
            startInfo.Environment["TRAILING_SELECTIVE_BASE"] = trailingSelective ? new string('a', 40) : string.Empty;

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(0, $"change detection must succeed. stdout: {await stdout}; stderr: {await stderr}");

            return (await File.ReadAllLinesAsync(outputPath))
                .Where(line => line.Contains('=', StringComparison.Ordinal))
                .Select(line => line.Split('=', 2))
                .GroupBy(pair => pair[0], StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static async Task RunPython(string script)
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
        {
            WorkingDirectory = ArchitectureTestHelpers.ResolveRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, $"{script} must pass. stdout: {await stdout}; stderr: {await stderr}");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Join(ArchitectureTestHelpers.ResolveRepositoryRoot(), relativePath)).ReplaceLineEndings("\n");

    private static string Job(string workflow, string jobId)
    {
        var start = workflow.IndexOf($"\n  {jobId}:\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"job '{jobId}' must exist");
        var next = NextJob().Match(workflow, start + jobId.Length + 4);
        return next.Success ? workflow[(start + 1)..next.Index] : workflow[(start + 1)..];
    }

    private static string JobName(string workflow, string jobId)
    {
        var name = Regex.Match(Job(workflow, jobId), @"^    name: (?<name>.+)$", RegexOptions.Multiline);
        name.Success.Should().BeTrue($"certification lane '{jobId}' needs a static name");
        return name.Groups["name"].Value.Trim();
    }

    private static string IfOf(string job)
    {
        var condition = Regex.Match(job, @"^    if: (?<if>.+)$", RegexOptions.Multiline);
        return condition.Success ? condition.Groups["if"].Value : string.Empty;
    }

    private static string NeedsOf(string job)
    {
        var needs = Regex.Match(job, @"^    needs: (?<needs>.+)$", RegexOptions.Multiline);
        return needs.Success ? needs.Groups["needs"].Value : string.Empty;
    }

    [GeneratedRegex(@"^  CERTIFICATION_LANE_JOBS: '(?<jobs>[^']+)'$", RegexOptions.Multiline)]
    private static partial Regex LaneDeclaration();

    [GeneratedRegex(@"\n  [A-Za-z0-9_-]+:\n")]
    private static partial Regex NextJob();

    [GeneratedRegex(@"\$\{\{\s*(?<expr>[^}]+?)\s*\}\}")]
    private static partial Regex Expression();
}
