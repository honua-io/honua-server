// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Contract drift guard for the custom-code params seam (honua-server#2191): the SERVER
/// re-emits every <c>customcode.*</c> job-input parameter as an
/// <c>env.CUSTOMCODE_*</c> spec parameter at submit, and the IMAGE/HARNESS
/// (<c>docker/worker-customcode-python/harness</c>, built on
/// <c>feat/customcode-python-image</c>) reads each input from the matching
/// <c>CUSTOMCODE_*</c> container environment variable. The two halves were built in
/// parallel, so this test pins the param→env mapping against a checked-in snapshot of
/// the harness's jobspec field map. If the harness renames an env var (or the server
/// stops emitting one), this fails — the same drift discipline as the AWS-adapter
/// contract test (#108).
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class CustomCodeJobContractDriftTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef01234567";
    private const string FixtureFileName = "customcode-harness-jobspec-contract.json";

    /// <summary>
    /// The server's pinned <c>customcode.* → CUSTOMCODE_*</c> env-name map must equal,
    /// key for key, the harness's <c>CUSTOMCODE_* → spec-key</c> map (inverted). This is
    /// the load-bearing seam assertion: any divergence means the server emits an env var
    /// the harness does not read, or omits one it requires.
    /// </summary>
    [UnitTest]
    [Operation(Operations.Create)]
    public void ServerParameterToEnvMap_MatchesHarnessJobSpecContractFixture()
    {
        var fixture = LoadHarnessContractFixture();

        // Harness fixture: CUSTOMCODE_<ENV> -> <spec_key>. The server param key is
        // "customcode.<spec_key>"; the server env name is the CUSTOMCODE_<ENV> key.
        // Build the expected server map (customcode.<spec_key> -> CUSTOMCODE_<ENV>).
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (envName, specKey) in fixture.BodyEnvToSpecKey)
        {
            expected["customcode." + specKey] = envName;
        }

        CustomCodeJobContract.ParameterToEnv
            .Should()
            .BeEquivalentTo(
                expected,
                "the server's customcode.* -> env.CUSTOMCODE_* emission must match the "
                + $"harness's {FixtureFileName} field map ({fixture.HarnessFile} @ "
                + $"{fixture.HarnessCommit}); a mismatch silently breaks the params seam");

        // The CUSTOMCODE_<ENV> name must be the spec key uppercased — the exact rule the
        // harness uses (env.get("CUSTOMCODE_" + key.upper())). Pin it so a future server
        // env-name typo can't pass the map-equality check above against a drifted fixture.
        foreach (var (envName, specKey) in fixture.BodyEnvToSpecKey)
        {
            envName.Should().Be(
                "CUSTOMCODE_" + specKey.ToUpperInvariant(),
                "the harness derives the env var as CUSTOMCODE_<spec-key-upper>");
        }
    }

    /// <summary>
    /// The serving↔worker job-contract version (ADR-0060 #3b) must be pinned across the server
    /// const and the harness snapshot. The server injects <c>HONUA_CONTRACT_VERSION</c> env-only
    /// (never in the customcode.* body map), and both harnesses re-check it; a bump must move the
    /// server const, both harness consts, and this fixture together.
    /// </summary>
    [UnitTest]
    [Operation(Operations.Create)]
    public void ContractVersion_IsPinnedToHarnessFixture_AndEnvOnly()
    {
        var fixture = LoadHarnessContractFixture();

        CustomCodeJobContract.ContractVersion.Should().Be(
            fixture.ContractVersion,
            "the server's job-contract version must match the harness-supported version in "
            + $"{FixtureFileName}; bump the server const, both harness consts, and the fixture together");

        CustomCodeJobContract.ContractVersionEnvName.Should().Be(
            fixture.ContractVersionEnv,
            "the injected contract-version env var name must match the harness");

        // Env-only, exactly like the auth spine — never folded into the customcode.* body map.
        CustomCodeJobContract.ParameterToEnv.Values
            .Should().NotContain(CustomCodeJobContract.ContractVersionEnvName);
        fixture.BodyEnvToSpecKey.Keys
            .Should().NotContain(CustomCodeJobContract.ContractVersionEnvName);
    }

    /// <summary>
    /// The auth spine env names are env-only and intentionally NOT in the body map;
    /// pin them so they are not accidentally folded into the customcode.* pass-through
    /// (which would route the scoped token through the wrong, non-secret path).
    /// </summary>
    [UnitTest]
    [Operation(Operations.Create)]
    public void AuthSpineEnvNames_AreSeparateFromBodyMap_AndPinnedToHarness()
    {
        var fixture = LoadHarnessContractFixture();

        fixture.AuthSpineEnv.Should().Contain(CustomCodeJobContract.BaseUrlEnvName);
        fixture.AuthSpineEnv.Should().Contain(CustomCodeJobContract.JobTokenEnvName);

        CustomCodeJobContract.ParameterToEnv.Values
            .Should().NotContain(CustomCodeJobContract.BaseUrlEnvName);
        CustomCodeJobContract.ParameterToEnv.Values
            .Should().NotContain(CustomCodeJobContract.JobTokenEnvName);
    }

    /// <summary>
    /// End-to-end through the coordinator: after a successful submit the durable spec
    /// carries an <c>env.CUSTOMCODE_*</c> parameter for every supplied customcode.* input
    /// (so AwsBatchComputeBackend.BuildEnvironmentOverrides — which forwards ONLY env.*
    /// keys — surfaces them to the container as CUSTOMCODE_*). This is the regression
    /// guard for the original seam break: bare customcode.* keys never reached the
    /// container, so the harness failed closed with "Required job input is missing".
    /// </summary>
    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Coordinator_EmitsEveryCustomCodeInputAsEnvPassThrough()
    {
        var issuer = new ScopedJobTokenIssuer(
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ScopedJobTokenIssuer>.Instance);
        var coordinator = new CustomCodeSubmitCoordinator(issuer);

        var options = new CustomCodeOptions
        {
            RepoPolicy = CustomCodeRepoPolicy.OrgAllowlist,
            RepoAllowlist = ["github.com"],
            ApiBaseUrl = "https://api.honua.test",
            OutputPrefixRoot = "s3://honua-customcode/outputs"
        };

        var specParams = new Dictionary<string, string>
        {
            [CustomCodeJobContract.RuntimeParam] = "python",
            [CustomCodeJobContract.RepoUrlParam] = "https://github.com/honua-io/example.git",
            [CustomCodeJobContract.GitRefParam] = ValidSha,
            [CustomCodeJobContract.EntrypointParam] = "pkg.module:run",
            [CustomCodeJobContract.DepsManifestParam] = "requirements.txt",
            [CustomCodeJobContract.ParamsJsonParam] = """{"k":"v"}""",
            [CustomCodeJobContract.DeclaredScopeParam] = """[{"serviceId":"parcels","access":"read"}]"""
        };

        await coordinator.ValidateMintAndInjectAsync(
            "gp-cc-seam-1", OwnerPrincipal(), specParams, options, CancellationToken.None);

        // Every supplied customcode.* input is now mirrored to env.CUSTOMCODE_* with the
        // SAME value (output_prefix is server-set, so its env mirrors the clamped value).
        foreach (var (paramKey, envName) in CustomCodeJobContract.ParameterToEnv)
        {
            specParams.Should().ContainKey(paramKey, "test supplied/derived every input");
            var passThroughKey = "env." + envName;
            specParams.Should().ContainKey(passThroughKey,
                $"'{paramKey}' must be re-emitted as '{passThroughKey}' so the Batch pass-through forwards it");
            specParams[passThroughKey].Should().Be(specParams[paramKey],
                "the env pass-through must carry the exact customcode.* value");
        }

        // The server-set output prefix flows through to its env var too.
        specParams.Should().ContainKey("env." + CustomCodeJobContract.OutputPrefixEnvName)
            .WhoseValue.Should().Contain("gp-cc-seam-1");

        // Auth spine present and env-only.
        specParams.Should().ContainKey(CustomCodeJobContract.BaseUrlEnvParam);
        specParams.Should().ContainKey(CustomCodeJobContract.JobTokenEnvParam);
    }

    /// <summary>
    /// An optional, unsupplied customcode.* input must NOT be emitted as an empty env
    /// var — the harness treats a present-but-empty CUSTOMCODE_* the same as a supplied
    /// value, so emitting an empty declared_scope would change harness behavior.
    /// </summary>
    [UnitTest]
    [Operation(Operations.Create)]
    public async Task Coordinator_OmitsEnvForUnsuppliedOptionalInput()
    {
        var issuer = new ScopedJobTokenIssuer(
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<ScopedJobTokenIssuer>.Instance);
        var coordinator = new CustomCodeSubmitCoordinator(issuer);

        var options = new CustomCodeOptions
        {
            RepoPolicy = CustomCodeRepoPolicy.OrgAllowlist,
            RepoAllowlist = ["github.com"],
            ApiBaseUrl = "https://api.honua.test",
            OutputPrefixRoot = "s3://honua-customcode/outputs"
        };

        // No declared_scope supplied.
        var specParams = new Dictionary<string, string>
        {
            [CustomCodeJobContract.RuntimeParam] = "python",
            [CustomCodeJobContract.RepoUrlParam] = "https://github.com/honua-io/example.git",
            [CustomCodeJobContract.GitRefParam] = ValidSha,
            [CustomCodeJobContract.EntrypointParam] = "pkg.module:run",
            [CustomCodeJobContract.DepsManifestParam] = "requirements.txt"
        };

        await coordinator.ValidateMintAndInjectAsync(
            "gp-cc-seam-2", OwnerPrincipal(), specParams, options, CancellationToken.None);

        specParams.Should().NotContainKey("env." + CustomCodeJobContract.DeclaredScopeEnvName);
        specParams.Should().NotContainKey("env." + CustomCodeJobContract.ParamsJsonEnvName);
    }

    private static ClaimsPrincipal OwnerPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "alice"),
                new Claim("tenant_id", "tenant-A"),
                new Claim("permission", "read:parcels")
            ],
            "Test"));

    private static HarnessContractFixture LoadHarnessContractFixture()
    {
        // Path.Combine args are relative test fixture fragments; no rooted-segment risk.
        var path = Path.Join(AppContext.BaseDirectory, "Features", "Geoprocessing", FixtureFileName);
        File.Exists(path).Should().BeTrue($"the checked-in harness contract fixture must be copied to the test output: {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var bodyMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in root.GetProperty("body_env_to_spec_key").EnumerateObject())
        {
            bodyMap[prop.Name] = prop.Value.GetString()!;
        }

        var authSpine = new List<string>();
        foreach (var item in root.GetProperty("auth_spine_env").EnumerateArray())
        {
            authSpine.Add(item.GetString()!);
        }

        return new HarnessContractFixture(
            BodyEnvToSpecKey: bodyMap,
            AuthSpineEnv: authSpine,
            ContractVersion: root.GetProperty("contract_version").GetInt32(),
            ContractVersionEnv: root.GetProperty("contract_version_env").GetString()!,
            HarnessFile: root.GetProperty("harness_file").GetString()!,
            HarnessCommit: root.GetProperty("harness_commit").GetString()!);
    }

    private sealed record HarnessContractFixture(
        IReadOnlyDictionary<string, string> BodyEnvToSpecKey,
        IReadOnlyList<string> AuthSpineEnv,
        int ContractVersion,
        string ContractVersionEnv,
        string HarnessFile,
        string HarnessCommit);
}
