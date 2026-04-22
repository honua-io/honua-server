// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Eval;

/// <summary>
/// Resolves the fixture corpus backing a scenario run. Two implementations are
/// supported: the shared geospatial-mcp corpus (pointed to by
/// <c>HONUA_EVAL_CORPUS_PATH</c>) and the in-repo <c>tests/seed/seed.yaml</c>
/// baseline applied through <see cref="PostgresFixture"/>.
/// </summary>
public interface IEvalFixtureSource
{
    /// <summary>Identifier: <c>shared</c> or <c>local-seed</c>.</summary>
    string Id { get; }

    /// <summary>Corpus version string used in the report envelope.</summary>
    string CorpusVersion { get; }

    /// <summary>Resolved corpus path when applicable (shared corpus only).</summary>
    string? CorpusPath { get; }

    /// <summary>
    /// YAML seed file applied to the shared eval schema before the web host starts.
    /// </summary>
    string SeedPath { get; }
}

/// <summary>
/// Binds scenarios to the shared geospatial-mcp fixture corpus mounted at
/// <c>HONUA_EVAL_CORPUS_PATH</c>. Not yet populated in CI; in-repo seed is used
/// as the fallback when the env var is unset.
/// </summary>
public sealed class SharedCorpusFixtureSource : IEvalFixtureSource
{
    /// <summary>Name of the env var used to locate the shared corpus.</summary>
    public const string CorpusPathEnvVar = "HONUA_EVAL_CORPUS_PATH";

    /// <summary>Name of the env var used to report the shared corpus version.</summary>
    public const string CorpusVersionEnvVar = "HONUA_EVAL_CORPUS_VERSION";

    private SharedCorpusFixtureSource(string corpusPath, string corpusVersion, string seedPath)
    {
        CorpusPath = corpusPath;
        CorpusVersion = corpusVersion;
        SeedPath = seedPath;
    }

    /// <inheritdoc />
    public string Id => "shared";

    /// <inheritdoc />
    public string CorpusVersion { get; }

    /// <inheritdoc />
    public string? CorpusPath { get; }

    /// <inheritdoc />
    public string SeedPath { get; }

    /// <summary>
    /// Attempts to bind to the shared corpus via <see cref="CorpusPathEnvVar"/>. Returns
    /// <c>null</c> only when the env var is unset or blank. Throws
    /// <see cref="InvalidOperationException"/> when the env var points at a path that
    /// does not exist or does not contain a resolvable seed file.
    /// </summary>
    public static SharedCorpusFixtureSource? TryCreate()
    {
        var path = Environment.GetEnvironmentVariable(CorpusPathEnvVar);
        var version = Environment.GetEnvironmentVariable(CorpusVersionEnvVar);
        return TryCreate(path, version);
    }

    internal static SharedCorpusFixtureSource? TryCreate(string? path, string? version)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"{CorpusPathEnvVar} is set to '{path}', but that path does not exist.");
        }

        var seedPath = ResolveSeedPath(path)
            ?? throw new InvalidOperationException(
                $"{CorpusPathEnvVar} must point to a YAML seed file or a directory containing seed.yaml.");

        if (string.IsNullOrWhiteSpace(version))
        {
            version = "shared-unversioned";
        }

        return new SharedCorpusFixtureSource(path, version, seedPath);
    }

    private static string? ResolveSeedPath(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        var candidates = new[]
        {
            Path.Combine(path, "seed.yaml"),
            Path.Combine(path, "seed.yml"),
            Path.Combine(path, "tests", "seed", "seed.yaml"),
            Path.Combine(path, "tests", "seed", "seed.yml")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// Falls back to the in-repo <c>tests/seed/seed.yaml</c> baseline so the harness
/// runs locally without external fixture mounts. Satisfies the contract's
/// "consume shared fixtures instead of ad hoc local-only test data" AC through the
/// same <c>SeedRunner</c> used by other integration tests.
/// </summary>
public sealed class LocalSeedFixtureSource : IEvalFixtureSource
{
    /// <inheritdoc />
    public string Id => "local-seed";

    /// <inheritdoc />
    public string CorpusVersion => "seed.yaml@v1";

    /// <inheritdoc />
    public string? CorpusPath => null;

    /// <inheritdoc />
    public string SeedPath => Path.Combine("tests", "seed", "seed.yaml");
}
