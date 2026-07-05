// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.CustomCode.Harness;
using Xunit;

namespace Honua.CustomCode.Harness.Tests;

public sealed class JobSpecTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef01234567";

    private static Dictionary<string, string?> BaseEnv() => new(StringComparer.Ordinal)
    {
        ["CUSTOMCODE_RUNTIME"] = "dotnet",
        ["CUSTOMCODE_REPO_URL"] = "https://github.com/honua-io/example.git",
        ["CUSTOMCODE_GIT_REF"] = ValidSha,
        ["CUSTOMCODE_ENTRYPOINT"] = "MyTool::My.Namespace.BufferTool",
        ["CUSTOMCODE_DEPS_MANIFEST"] = "tool/MyTool.csproj",
        ["CUSTOMCODE_PARAMS_JSON"] = """{"k":"v"}""",
        ["CUSTOMCODE_OUTPUT_PREFIX"] = "s3://bucket/outputs/job-1",
        ["HONUA_BASE_URL"] = "https://api.honua.test",
        ["HONUA_JOB_TOKEN"] = "scoped-token",
    };

    [Fact]
    public void Load_FullSha_Accepted_ParsesEntrypointParts()
    {
        var spec = JobSpec.Load(BaseEnv());

        spec.Runtime.Should().Be("dotnet");
        spec.GitRef.Should().Be(ValidSha);
        spec.EntrypointAssembly.Should().Be("MyTool");
        spec.EntrypointType.Should().Be("My.Namespace.BufferTool");
        spec.OutputMaxBytes.Should().Be(JobSpec.DefaultOutputMaxBytes);
        spec.Params.GetProperty("k").GetString().Should().Be("v");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("0123456789abcdef")] // abbreviated
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")] // uppercase
    [InlineData("v1.2.3")]
    public void Load_NonShaGitRef_Rejected(string gitRef)
    {
        var env = BaseEnv();
        env["CUSTOMCODE_GIT_REF"] = gitRef;

        var act = () => JobSpec.Load(env);

        act.Should().Throw<JobSpecException>().WithMessage("*40-character hex commit SHA*");
    }

    [Theory]
    [InlineData("pkg.module:run")]   // python form
    [InlineData("MyTool::")]          // missing type
    [InlineData("::Type")]            // missing assembly
    [InlineData("My Tool::Type")]     // space
    [InlineData("MyTool::My-Type")]   // invalid type char
    public void Load_BadDotnetEntrypoint_Rejected(string entrypoint)
    {
        var env = BaseEnv();
        env["CUSTOMCODE_ENTRYPOINT"] = entrypoint;

        var act = () => JobSpec.Load(env);

        act.Should().Throw<JobSpecException>().WithMessage("*Assembly::Namespace.Type*");
    }

    [Fact]
    public void Load_DepsManifestTraversal_Rejected()
    {
        var env = BaseEnv();
        env["CUSTOMCODE_DEPS_MANIFEST"] = "../../etc/evil.csproj";

        var act = () => JobSpec.Load(env);

        act.Should().Throw<JobSpecException>().WithMessage("*relative path within the repository*");
    }

    [Fact]
    public void Load_NonS3OutputPrefix_Rejected()
    {
        var env = BaseEnv();
        env["CUSTOMCODE_OUTPUT_PREFIX"] = "file:///tmp/out";

        var act = () => JobSpec.Load(env);

        act.Should().Throw<JobSpecException>().WithMessage("*s3://*");
    }

    [Fact]
    public void Load_MissingToken_Rejected()
    {
        var env = BaseEnv();
        env.Remove("HONUA_JOB_TOKEN");

        var act = () => JobSpec.Load(env);

        act.Should().Throw<JobSpecException>().WithMessage("*HONUA_JOB_TOKEN*");
    }

    [Fact]
    public void Load_EmptyParams_DefaultsToEmptyObject()
    {
        var env = BaseEnv();
        env.Remove("CUSTOMCODE_PARAMS_JSON");

        var spec = JobSpec.Load(env);

        spec.Params.ValueKind.Should().Be(JsonValueKind.Object);
        spec.Params.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void Load_NoContractVersion_DefaultsToOne()
    {
        var spec = JobSpec.Load(BaseEnv());

        spec.ContractVersion.Should().Be(1);
    }

    [Fact]
    public void Load_ContractVersionAtSupported_Accepted()
    {
        var env = BaseEnv();
        env["HONUA_CONTRACT_VERSION"] = JobSpec.SupportedContractVersion.ToString();

        var spec = JobSpec.Load(env);

        spec.ContractVersion.Should().Be(JobSpec.SupportedContractVersion);
    }

    [Fact]
    public void Load_ContractVersionAboveSupported_Rejected()
    {
        var env = BaseEnv();
        env["HONUA_CONTRACT_VERSION"] = (JobSpec.SupportedContractVersion + 1).ToString();

        var act = () => JobSpec.Load(env);

        act.Should().Throw<JobSpecException>().WithMessage("*HONUA_CONTRACT_VERSION*exceeds*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void Load_ContractVersionNonPositiveOrNonInteger_Rejected(string value)
    {
        var env = BaseEnv();
        env["HONUA_CONTRACT_VERSION"] = value;

        var act = () => JobSpec.Load(env);

        act.Should().Throw<JobSpecException>().WithMessage("*HONUA_CONTRACT_VERSION*");
    }

    [Fact]
    public void Load_JobSpecFile_ReadsFields_ButAuthStaysEnvOnly()
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-spec");
        try
        {
            var specPath = Path.Combine(dir.FullName, "spec.json");
            File.WriteAllText(specPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["runtime"] = "dotnet",
                ["repo_url"] = "https://github.com/honua-io/example.git",
                ["git_ref"] = ValidSha,
                ["entrypoint"] = "MyTool::My.Namespace.BufferTool",
                ["deps_manifest"] = "tool/MyTool.csproj",
                ["output_prefix"] = "s3://bucket/outputs/job-1",
            }));

            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["CUSTOMCODE_JOB_SPEC"] = specPath,
                // Auth spine is ALWAYS env-only.
                ["HONUA_BASE_URL"] = "https://api.honua.test",
                ["HONUA_JOB_TOKEN"] = "scoped-token",
            };

            var spec = JobSpec.Load(env);

            spec.RepoUrl.Should().Be("https://github.com/honua-io/example.git");
            spec.JobToken.Should().Be("scoped-token");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_JobSpecFile_MissingAuthEnv_Rejected()
    {
        var dir = Directory.CreateTempSubdirectory("ccnet-spec");
        try
        {
            var specPath = Path.Combine(dir.FullName, "spec.json");
            // The token is (incorrectly) placed in the spec file — it must NOT be honored.
            File.WriteAllText(specPath, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["repo_url"] = "https://github.com/honua-io/example.git",
                ["git_ref"] = ValidSha,
                ["entrypoint"] = "MyTool::My.Namespace.BufferTool",
                ["deps_manifest"] = "tool/MyTool.csproj",
                ["output_prefix"] = "s3://bucket/outputs/job-1",
                ["HONUA_JOB_TOKEN"] = "smuggled",
            }));

            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["CUSTOMCODE_JOB_SPEC"] = specPath,
                ["HONUA_BASE_URL"] = "https://api.honua.test",
            };

            var act = () => JobSpec.Load(env);

            act.Should().Throw<JobSpecException>().WithMessage("*HONUA_JOB_TOKEN*");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
