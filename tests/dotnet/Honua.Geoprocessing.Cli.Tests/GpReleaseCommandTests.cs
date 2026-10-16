// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing.Cli.Release;
using Honua.Geoprocessing.Release;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// <c>honua-gp-devkit release</c> against a real Git checkout: the same descriptor at the same
/// commit must produce the same lock bytes and the same artifact bytes every time (that is what
/// makes a re-run a no-op), the lock must validate against the v1 contract, and every way the
/// inputs can be wrong (dirty tree, wrong SHA, missing receipt, bad descriptor) must refuse with a
/// named finding instead of writing a lock.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public sealed class GpReleaseCommandTests : IDisposable
{
    private const string RuntimeImage = "ghcr.io/honua-io/honua-gp-worker@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _root = Path.Join(Path.GetTempPath(), "honua-gp-release-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; the temp directory is per-test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort; the temp directory is per-test.
        }
    }

    [Fact]
    public void Release_IsDeterministicAndVerifies()
    {
        var project = CreateProject();
        var sha = Git(project, "rev-parse", "HEAD");

        var first = Run(project, "out1");
        var second = Run(project, "out2");

        first.Exit.Should().Be(0, first.Stderr);
        second.Exit.Should().Be(0, second.Stderr);
        var lock1 = File.ReadAllText(Path.Join(project, "out1", "process-release-lock.json"));
        var lock2 = File.ReadAllText(Path.Join(project, "out2", "process-release-lock.json"));
        lock2.Should().Be(lock1, "the same descriptor at the same commit must lock to the same bytes");
        File.ReadAllBytes(Path.Join(project, "out1", "artifacts", "summary-tool-1.2.0.tar.gz"))
            .Should().Equal(File.ReadAllBytes(Path.Join(project, "out2", "artifacts", "summary-tool-1.2.0.tar.gz")));

        var parsed = GpProcessReleaseLock.Parse(lock1);
        parsed.Source.Sha.Should().Be(sha);
        parsed.Source.Repository.Should().Be("honua-io/honua-samples");
        parsed.Source.DescriptorPath.Should().Be("tools/summary/honua-gp.yaml");
        parsed.DependencyLock.Path.Should().Be("tools/summary/requirements.lock");
        parsed.RuntimeImage.Reference.Should().Be(RuntimeImage);
        parsed.Targets.Should().ContainSingle().Which.WorkerImageDigest.Should().Be("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        parsed.ValidationReceipts.Should().ContainSingle().Which.Name.Should().Be("golden-tests.json");
        parsed.GeneratedAt.Should().MatchRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$");
        first.Stdout.Should().Contain("digest  : " + parsed.Digest);

        var verify = RunVerify(Path.Join(project, "out1", "process-release-lock.json"), Path.Join(project, "out1", "artifacts"));
        verify.Exit.Should().Be(0, verify.Stderr);
        verify.Stdout.Should().Contain(parsed.Digest).And.Contain("1 match the lock");

        using var document = JsonDocument.Parse(RunJson(project, "out3").Stdout);
        document.RootElement.GetProperty("digest").GetString().Should().Be(parsed.Digest);
    }

    [Fact]
    public void Release_ArtifactHoldsOnlyTheIncludedFilesInOrder()
    {
        var project = CreateProject();

        Run(project, "out").Exit.Should().Be(0);

        using var file = File.OpenRead(Path.Join(project, "out", "artifacts", "summary-tool-1.2.0.tar.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            entry.Mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        names.Should().Equal("process/summary_tool.py", "requirements.lock", "schema/inputs.schema.json", "schema/outputs.schema.json");
    }

    [Fact]
    public void Release_RefusesADirtyCheckout()
    {
        var project = CreateProject();
        File.AppendAllText(Path.Join(project, "tools", "summary", "process", "summary_tool.py"), "\n# edited\n");

        var result = Run(project, "out");

        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("uncommitted changes").And.Contain("summary_tool.py");
        File.Exists(Path.Join(project, "out", "process-release-lock.json")).Should().BeFalse();
    }

    [Fact]
    public void Release_RefusesASourceShaThatIsNotCheckedOut()
    {
        var project = CreateProject();

        var result = Run(project, "out", "--source-sha", "0123456789abcdef0123456789abcdef01234567");

        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("is not the checked-out commit");
    }

    [Fact]
    public void Release_RefusesAMissingReceipt()
    {
        var project = CreateProject(receiptPath: "artifacts/missing.xml");

        var result = Run(project, "out");

        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("validation.receipts[golden-tests.json]").And.Contain("does not exist");
        File.Exists(Path.Join(project, "out", "process-release-lock.json")).Should().BeFalse();
    }

    [Fact]
    public void Release_RefusesAConfigurationValueInsteadOfAReference()
    {
        var project = CreateProject(configurationReference: "s3://bucket/value");

        var result = Run(project, "out");

        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("configuration[HONUA_DATA_BUCKET].reference").And.Contain("never a value");
    }

    [Fact]
    public void Release_RequiresTheOutputDirectoryAndRejectsUnknownOptions()
    {
        var project = CreateProject();

        var missing = () => GpReleaseCommand.Run(["--descriptor", Path.Join(project, "tools", "summary", "honua-gp.yaml")], TextWriter.Null, TextWriter.Null);
        missing.Should().Throw<GpCliUsageException>().WithMessage("*--output*");

        var unknown = () => GpReleaseCommand.Run(["--bogus"], TextWriter.Null, TextWriter.Null);
        unknown.Should().Throw<GpCliUsageException>().WithMessage("*--bogus*");
    }

    [Fact]
    public void Verify_FlagsAnArtifactWhoseBytesChanged()
    {
        var project = CreateProject();
        Run(project, "out").Exit.Should().Be(0);
        File.WriteAllBytes(Path.Join(project, "out", "artifacts", "summary-tool-1.2.0.tar.gz"), [1, 2, 3]);

        var verify = RunVerify(Path.Join(project, "out", "process-release-lock.json"), Path.Join(project, "out", "artifacts"));

        verify.Exit.Should().Be(1);
        verify.Stderr.Should().Contain("summary-tool-1.2.0.tar.gz").And.Contain("does not match the lock");
    }

    [Theory]
    [InlineData("requirements.lock")]
    [InlineData("schema/inputs.schema.json")]
    [InlineData("schema/outputs.schema.json")]
    public void Release_RefusesDirtyHashedInputsOutsideArtifactIncludes(string relative)
    {
        var project = CreateProject();
        var tool = Path.Join(project, "tools", "summary");
        var descriptor = Path.Join(tool, "honua-gp.yaml");
        File.WriteAllText(descriptor, File.ReadAllText(descriptor)
            .Replace("    - schema\n", "", StringComparison.Ordinal)
            .Replace("    - requirements.lock\n", "", StringComparison.Ordinal));
        Git(project, "add", ".");
        Git(project, "commit", "-qm", "narrow artifact inputs");
        File.AppendAllText(Path.Join(tool, relative), "\nchanged");

        var result = Run(project, "out");

        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("uncommitted changes").And.Contain(Path.GetFileName(relative));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Release_DefaultIncludesExcludeNestedOutputOnRerun(bool ignored)
    {
        var project = CreateProject();
        var descriptor = Path.Join(project, "tools", "summary", "honua-gp.yaml");
        var text = File.ReadAllText(descriptor);
        var start = text.IndexOf("artifacts:", StringComparison.Ordinal);
        var end = text.IndexOf("validation:", start, StringComparison.Ordinal);
        File.WriteAllText(descriptor, text.Remove(start, end - start));
        if (!ignored)
        {
            File.WriteAllText(Path.Join(project, ".gitignore"), "");
        }
        Git(project, "add", ".");
        Git(project, "commit", "-qm", "use default includes");
        WriteReceipt(project);
        const string output = "tools/summary/out-release";
        var first = Run(project, output);
        first.Exit.Should().Be(0, first.Stderr);
        var lockPath = Path.Join(project, output, "process-release-lock.json");
        var original = File.ReadAllBytes(lockPath);

        var second = Run(project, output);

        second.Exit.Should().Be(0, second.Stderr);
        File.ReadAllBytes(lockPath).Should().Equal(original);
    }

    [Theory]
    [InlineData("2026-02-31T00:00:00Z")]
    [InlineData("2026-10-15T25:00:00Z")]
    public void Release_InvalidTimestampReturnsFindingBeforeWriting(string timestamp)
    {
        var project = CreateProject();
        var result = Run(project, "out", "--generated-at", timestamp);
        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("--generated-at");
        Directory.Exists(Path.Join(project, "out")).Should().BeFalse();
    }

    [Fact]
    public void Release_FractionalTimestampIsAcceptedAndDeterministic()
    {
        var project = CreateProject();
        const string timestamp = "2026-10-15T04:00:00.123Z";
        var first = Run(project, "out1", "--generated-at", timestamp);
        var second = Run(project, "out2", "--generated-at", timestamp);
        first.Exit.Should().Be(0, first.Stderr);
        second.Exit.Should().Be(0, second.Stderr);
        File.ReadAllBytes(Path.Join(project, "out2", "process-release-lock.json"))
            .Should().Equal(File.ReadAllBytes(Path.Join(project, "out1", "process-release-lock.json")));
    }

    [Fact]
    public void Release_RefusesSiblingPrefixPath()
    {
        var project = CreateProject();
        var tool = Path.Join(project, "tools", "summary");
        Directory.CreateDirectory(Path.Join(project, "tools", "summary-extra"));
        File.WriteAllText(Path.Join(project, "tools", "summary-extra", "requirements.lock"), "outside");
        var descriptor = Path.Join(tool, "honua-gp.yaml");
        File.WriteAllText(descriptor, File.ReadAllText(descriptor).Replace(
            "dependencyLock: requirements.lock", "dependencyLock: ../summary-extra/requirements.lock", StringComparison.Ordinal));
        Git(project, "add", ".");
        Git(project, "commit", "-qm", "reference sibling");

        var result = Run(project, "out");

        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("leaves the project directory");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Release_RefusesTrackedFileAndDirectorySymlinks(bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating links requires a privilege not present on every Windows runner.
        }
        var project = CreateProject();
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "private.txt"), "must not be packaged");
        var link = Path.Join(project, "tools", "summary", "process", "linked");
        if (directory)
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        else
        {
            File.CreateSymbolicLink(link, Path.Join(outside, "private.txt"));
        }
        Git(project, "add", ".");
        Git(project, "commit", "-qm", "include link");

        var result = Run(project, "out");

        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("symlink");
        Directory.Exists(Path.Join(project, "out")).Should().BeFalse();
    }

    private (int Exit, string Stdout, string Stderr) Run(string project, string output, params string[] extra)
    {
        var args = new List<string>
        {
            "--descriptor", Path.Join(project, "tools", "summary", "honua-gp.yaml"),
            "--output", Path.Join(project, output),
            "--source-repository", "honua-io/honua-samples",
        };
        args.AddRange(extra);
        return Invoke(args.ToArray());
    }

    private (int Exit, string Stdout, string Stderr) RunJson(string project, string output)
        => Run(project, output, "--json");

    private static (int Exit, string Stdout, string Stderr) RunVerify(string lockPath, string artifacts)
        => Invoke(["verify", lockPath, "--artifacts", artifacts]);

    private static (int Exit, string Stdout, string Stderr) Invoke(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = GpReleaseCommand.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string CreateProject(string receiptPath = "artifacts/golden-tests.json", string configurationReference = "ref:environment/data-bucket")
    {
        var project = Path.Join(_root, "repo");
        var tool = Path.Join(project, "tools", "summary");
        Directory.CreateDirectory(Path.Join(tool, "process"));
        Directory.CreateDirectory(Path.Join(tool, "schema"));
        Directory.CreateDirectory(Path.Join(tool, "artifacts"));
        File.WriteAllText(Path.Join(tool, "process", "summary_tool.py"), "def execute(context):\n    return {\"sum\": 30}\n");
        File.WriteAllText(Path.Join(tool, "schema", "inputs.schema.json"), "{\"type\":\"object\"}\n");
        File.WriteAllText(Path.Join(tool, "schema", "outputs.schema.json"), "{\"type\":\"object\",\"required\":[\"sum\"]}\n");
        File.WriteAllText(Path.Join(tool, "requirements.lock"), "honua-customcode-harness==0.1.0\n");
        File.WriteAllText(Path.Join(tool, "honua-gp.yaml"), $"""
            schema: honua.gp-process/v1
            process:
              name: summary-tool
              version: 1.2.0
              runtime: python
              entrypoint: process.summary_tool:execute
              contract:
                inputSchema: schema/inputs.schema.json
                outputSchema: schema/outputs.schema.json
            dependencyLock: requirements.lock
            runtimeImage: {RuntimeImage}
            workflowVersion: wf-2026.1-7
            configuration:
              - name: HONUA_DATA_BUCKET
                reference: {configurationReference}
            targets:
              - kind: kubernetes-job
                chartVersion: 2026.1.0
            compatibility:
              serverContractVersion:
                minimum: 3
                maximum: 4
            artifacts:
              include:
                - process
                - schema
                - requirements.lock
            validation:
              receipts:
                - name: golden-tests.json
                  path: {receiptPath}

            """);
        File.WriteAllText(Path.Join(project, ".gitignore"), "out*/\nartifacts/\n");

        Git(project, "init", "-q");
        Git(project, "config", "user.email", "test@example.invalid");
        Git(project, "config", "user.name", "Test");
        Git(project, "config", "commit.gpgsign", "false");
        Git(project, "add", ".");
        Git(project, "commit", "-qm", "summary tool 1.2.0");
        WriteReceipt(project);
        return project;
    }

    [Fact]
    public void Release_TargetImageDiffersFromValidatedRuntime_RefusesUntestedDeployment()
    {
        var project = CreateProject();
        var descriptor = Path.Join(project, "tools", "summary", "honua-gp.yaml");
        File.WriteAllText(descriptor, File.ReadAllText(descriptor).Replace(
            "    chartVersion: 2026.1.0", "    chartVersion: 2026.1.0\n    workerImageDigest: sha256:" + new string('c', 64), StringComparison.Ordinal));
        Git(project, "add", ".");
        Git(project, "commit", "-qm", "select different target image");
        WriteReceipt(project);
        var result = Run(project, "out");
        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("workerImageDigest must match the receipt-validated runtime image digest");
    }

    private static void WriteReceipt(string project, string status = "pass", string? sourceSha = null,
        string? runtimeDigest = null, int passed = 4, int failed = 0, int skipped = 0)
        => File.WriteAllText(Path.Join(project, "tools", "summary", "artifacts", "golden-tests.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "honua.gp-validation/v1",
                status,
                sourceSha = sourceSha ?? Git(project, "rev-parse", "HEAD").Trim(),
                runtimeImageDigest = runtimeDigest ?? RuntimeImage[(RuntimeImage.IndexOf('@') + 1)..],
                tests = new { passed, failed, skipped }
            }));

    [Theory]
    [InlineData("failed")]
    [InlineData("wrong-source")]
    [InlineData("wrong-image")]
    [InlineData("no-tests")]
    [InlineData("failed-test")]
    [InlineData("skipped-test")]
    [InlineData("malformed")]
    public void Release_RefusesUnverifiedOrWrongCandidateReceipt(string failure)
    {
        var project = CreateProject();
        WriteReceipt(project,
            status: failure == "failed" ? "fail" : "pass",
            sourceSha: failure == "wrong-source" ? new string('a', 40) : null,
            runtimeDigest: failure == "wrong-image" ? "sha256:" + new string('c', 64) : null,
            passed: failure == "no-tests" ? 0 : 4,
            failed: failure == "failed-test" ? 1 : 0,
            skipped: failure == "skipped-test" ? 1 : 0);
        if (failure == "malformed")
        {
            File.WriteAllText(Path.Join(project, "tools", "summary", "artifacts", "golden-tests.json"),
                "<testsuite tests=\"4\" failures=\"1\"/>");
        }
        var result = Run(project, "out");
        result.Exit.Should().Be(1);
        result.Stderr.Should().Contain("validation.receipts[golden-tests.json]");
        File.Exists(Path.Join(project, "out", "process-release-lock.json")).Should().BeFalse();
    }

    private static string Git(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout.Trim();
    }
}
