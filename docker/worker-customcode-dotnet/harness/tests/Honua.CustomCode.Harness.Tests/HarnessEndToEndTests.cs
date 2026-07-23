// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.CustomCode.Harness;
using Honua.CustomCode.Sdk;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

// Tool types used by the E2E tests are TOP-LEVEL (not nested) so their CLR full names
// are dotted (Namespace.Type), matching the "Assembly::Namespace.Type" entrypoint form
// the harness validates and loads.
public sealed class ProbeTool : IGeoprocessingTool
{
    // Set by the test before the run; the tool records what it observed.
    // Intentionally static: the harness activates this type via reflection
    // (EntrypointLoader/Activator.CreateInstance) with a parameterless constructor, so
    // there is no seam to inject per-test state or read back a per-instance result —
    // these statics are the only channel between the test and the activated instance.
    public static IDictionary<string, string?>? ObservedEnv;
    public static object? ObservedClient;
    public static bool? TokenPresentAtExecute;
    public static string? ArtifactToWrite;

    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
    {
        // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
        ObservedClient = context.Client;
        // At execute time the credential env must already be scrubbed.
        // codeql[cs/static-field-written-by-instance] -- the instance lifecycle intentionally coordinates shared process-wide state.
        TokenPresentAtExecute = ObservedEnv is not null && ObservedEnv.ContainsKey("HONUA_JOB_TOKEN");

        if (ArtifactToWrite is not null)
        {
            // False positive: ArtifactToWrite is always a test-set relative literal
            // (e.g. "out.geojson"), never rooted.
            var path = Path.Join(context.WorkDirectory, ArtifactToWrite);
            File.WriteAllText(path, "result-bytes");
            context.Output.AddArtifact(ArtifactToWrite, path);
        }

        context.Progress.Report(100.0, "done");
        return Task.FromResult(GpResult.Succeeded("probe ok"));
    }
}

public sealed class FailingTool : IGeoprocessingTool
{
    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
        => Task.FromResult(GpResult.Failed("nope"));
}

public sealed class ThrowingTool : IGeoprocessingTool
{
    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException("boom");
}

public sealed class CancellingTool : IGeoprocessingTool
{
    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
        => throw new OperationCanceledException();
}

public sealed class HarnessEndToEndTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef01234567";

    private static HarnessOptions BuildOptions(
        IDictionary<string, string?> env,
        out List<(string Bucket, string Key, string Path)> uploads)
    {
        var workRoot = Directory.CreateTempSubdirectory("ccnet-e2e");
        var capturedUploads = new List<(string, string, string)>();
        uploads = capturedUploads;

        // Stage the user .csproj so UserProjectBuilder's real path/existence checks pass;
        // the injected dotnet runner below stubs the actual compile.
        // Path.Combine args below are a temp-dir root plus literal relative segments; no
        // rooted-segment risk (cs/path-combine false positive).
        var sourceRoot = Path.Join(workRoot.FullName, "src");
        Directory.CreateDirectory(Path.Join(sourceRoot, "tool"));
        File.WriteAllText(Path.Join(sourceRoot, "tool", "MyTool.csproj"), "<Project />");

        return new HarnessOptions
        {
            SourceRoot = Path.Join(workRoot.FullName, "src"),
            BuildOutput = Path.Join(workRoot.FullName, "build"),
            WorkDir = Path.Join(workRoot.FullName, "out"),
            // Fake git: succeed, and report HEAD == requested SHA.
            SourceFetch = new SourceFetch((args, _) =>
                args is ["rev-parse", "HEAD"] ? new ProcessResult(0, ValidSha, "") : new ProcessResult(0, "", "")),
            // Fake build: just succeed (the loader is also injected, so no real assembly is needed).
            ProjectBuilder = new UserProjectBuilder((_, _) => new ProcessResult(0, "build ok", "")),
            // The entrypoint loader resolves tool types from THIS test assembly.
            EntrypointLoader = new EntrypointLoader(_ => typeof(HarnessEndToEndTests).Assembly),
            // Fake scoped-client factory: records that it was called with the token.
            ClientFactory = (_, token) => new TestScopedClient(token),
            // Fake uploader records puts.
            UploaderFactory = prefix => new ArtifactUploader(prefix, (b, k, p) => capturedUploads.Add((b, k, p))),
            MutableEnv = env,
            StripEnvironment = true,
        };
    }

    private sealed class TestScopedClient(string token)
    {
        public string Token { get; } = token;
    }

    private static Dictionary<string, string?> JobEnv() => new(StringComparer.Ordinal)
    {
        ["CUSTOMCODE_RUNTIME"] = "dotnet",
        ["CUSTOMCODE_REPO_URL"] = "https://github.com/honua-io/example.git",
        ["CUSTOMCODE_GIT_REF"] = ValidSha,
        ["CUSTOMCODE_ENTRYPOINT"] = "MyTool::My.Namespace.BufferTool",
        ["CUSTOMCODE_DEPS_MANIFEST"] = "tool/MyTool.csproj",
        ["CUSTOMCODE_PARAMS_JSON"] = """{"wkt":"POINT (1 1)"}""",
        ["CUSTOMCODE_OUTPUT_PREFIX"] = "s3://bucket/outputs/job-1",
        ["HONUA_BASE_URL"] = "https://api.honua.test",
        ["HONUA_JOB_TOKEN"] = "scoped-token",
        ["AWS_CONTAINER_CREDENTIALS_RELATIVE_URI"] = "/v2/credentials/abc",
    };

    [Fact]
    public async Task Run_HappyPath_StripsCredsBeforeTool_BuildsClient_UploadsArtifact_ReturnsOk()
    {
        var env = JobEnv();
        ProbeTool.ObservedEnv = env;
        ProbeTool.TokenPresentAtExecute = null;
        ProbeTool.ObservedClient = null;
        ProbeTool.ArtifactToWrite = "out.geojson";

        var options = BuildOptions(env, out var uploads);
        // Point the entrypoint at the probe tool living in this test assembly.
        env["CUSTOMCODE_ENTRYPOINT"] = $"TestAsm::{typeof(ProbeTool).FullName}";

        var harness = new Harness(options);
        var exit = await harness.RunAsync(env);

        exit.Should().Be(Harness.ExitOk);

        // The scoped client was constructed from the token (and handed to the tool).
        ProbeTool.ObservedClient.Should().BeOfType<TestScopedClient>()
            .Which.Token.Should().Be("scoped-token");

        // The credential env was scrubbed BEFORE the tool ran.
        ProbeTool.TokenPresentAtExecute.Should().BeFalse();
        env.Should().NotContainKey("HONUA_JOB_TOKEN");
        env.Should().NotContainKey("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI");
        env.Should().ContainKey("HONUA_BASE_URL"); // non-credential env preserved.

        // The artifact was uploaded under the per-job prefix.
        uploads.Should().ContainSingle();
        uploads[0].Key.Should().Be("outputs/job-1/out.geojson");
    }

    [Fact]
    public async Task Run_BadInputs_ReturnsHarnessError()
    {
        var env = JobEnv();
        env["CUSTOMCODE_GIT_REF"] = "main"; // not a SHA
        var options = BuildOptions(env, out _);

        var exit = await new Harness(options).RunAsync(env);

        exit.Should().Be(Harness.ExitHarnessError);
    }

    [Fact]
    public async Task Run_ToolReportsFailure_ReturnsToolFailed()
    {
        var env = JobEnv();
        env["CUSTOMCODE_ENTRYPOINT"] = $"TestAsm::{typeof(FailingTool).FullName}";
        var options = BuildOptions(env, out _);
        options.EntrypointLoader = new EntrypointLoader(_ => typeof(HarnessEndToEndTests).Assembly);

        var exit = await new Harness(options).RunAsync(env);

        exit.Should().Be(Harness.ExitToolFailed);
    }

    [Fact]
    public async Task Run_ToolThrows_ReturnsToolFailed()
    {
        var env = JobEnv();
        env["CUSTOMCODE_ENTRYPOINT"] = $"TestAsm::{typeof(ThrowingTool).FullName}";
        var options = BuildOptions(env, out _);
        options.EntrypointLoader = new EntrypointLoader(_ => typeof(HarnessEndToEndTests).Assembly);

        var exit = await new Harness(options).RunAsync(env);

        exit.Should().Be(Harness.ExitToolFailed);
    }

    [Fact]
    public async Task Run_Cancelled_ReturnsCancelled()
    {
        var env = JobEnv();
        env["CUSTOMCODE_ENTRYPOINT"] = $"TestAsm::{typeof(CancellingTool).FullName}";
        var options = BuildOptions(env, out _);
        options.EntrypointLoader = new EntrypointLoader(_ => typeof(HarnessEndToEndTests).Assembly);

        var exit = await new Harness(options).RunAsync(env);

        exit.Should().Be(Harness.ExitCancelled);
    }
}
