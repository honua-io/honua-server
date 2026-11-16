// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit.Eval;

public interface IEvalFixtureSource
{
    string Id { get; }

    string CorpusVersion { get; }

    string? CorpusPath { get; }

    string SeedPath { get; }
}

public sealed class SharedCorpusFixtureSource : IEvalFixtureSource
{
    public const string CorpusPathEnvVar = "HONUA_EVAL_CORPUS_PATH";

    public const string CorpusVersionEnvVar = "HONUA_EVAL_CORPUS_VERSION";

    private SharedCorpusFixtureSource(string corpusPath, string corpusVersion, string seedPath)
    {
        CorpusPath = corpusPath;
        CorpusVersion = corpusVersion;
        SeedPath = seedPath;
    }

    public string Id => "shared";

    public string CorpusVersion { get; }

    public string? CorpusPath { get; }

    public string SeedPath { get; }

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

        // Each Path.Combine call below joins `path` with fixed literal segments, so
        // `path` can't be dropped by an unexpectedly-rooted later argument.
        var candidates = new[]
        {
            Path.Join(path, "seed.yaml"),
            Path.Join(path, "seed.yml"),
            Path.Join(path, "tests", "seed", "seed.yaml"),
            Path.Join(path, "tests", "seed", "seed.yml")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class LocalSeedFixtureSource : IEvalFixtureSource
{
    public string Id => "local-seed";

    public string CorpusVersion => "seed.yaml@v1";

    public string? CorpusPath => null;

    // Both segments are fixed literals, so this combine can never drop an argument.
    public string SeedPath => Path.Join("tests", "seed", "seed.yaml");
}
