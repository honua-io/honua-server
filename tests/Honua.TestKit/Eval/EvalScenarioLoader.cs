// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.TestKit.Eval;

/// <summary>
/// AOT-safe loader that deserializes <see cref="EvalScenario"/> documents through the
/// source-generated <see cref="EvalJsonContext"/>. Resolves scenario paths relative
/// to the nearest <c>Honua.sln</c> or <c>tests/Eval/scenarios/</c> root.
/// </summary>
public static class EvalScenarioLoader
{
    private const string ScenarioRootSegment = "tests/Eval/scenarios";

    /// <summary>
    /// Loads a single scenario by identifier. Searches (in order):
    /// <list type="number">
    ///   <item>Override path from the <c>HONUA_EVAL_SCENARIO_ROOT</c> environment variable.</item>
    ///   <item>Co-located scenarios under the solution's <c>tests/Eval/scenarios/</c> directory.</item>
    ///   <item>Co-located scenarios next to the test binary.</item>
    /// </list>
    /// </summary>
    public static EvalScenario LoadById(string scenarioId)
    {
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            throw new EvalScenarioException("Scenario id must be non-empty.");
        }

        var path = ResolveScenarioPath(scenarioId)
            ?? throw new EvalScenarioException(
                $"Scenario '{scenarioId}' not found. Searched {ScenarioRootSegment} relative to the solution root and test binary.");

        try
        {
            using var stream = File.OpenRead(path);
            var scenario = JsonSerializer.Deserialize(stream, EvalJsonContext.Default.EvalScenario);
            if (scenario == null)
            {
                throw new EvalScenarioException($"Scenario '{scenarioId}' at '{path}' deserialized to null.");
            }

            if (!string.Equals(scenario.Id, scenarioId, StringComparison.OrdinalIgnoreCase))
            {
                throw new EvalScenarioException(
                    $"Scenario id mismatch: file declares '{scenario.Id}' but was loaded as '{scenarioId}'.");
            }

            return scenario;
        }
        catch (JsonException ex)
        {
            throw new EvalScenarioException($"Failed to parse scenario '{scenarioId}' at '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Enumerates all scenario identifiers discoverable under the scenario root.
    /// Used by CI reporting and the xUnit host to iterate the full corpus.
    /// </summary>
    public static IReadOnlyList<string> DiscoverScenarioIds()
    {
        var root = ResolveScenarioRoot();
        if (root == null || !Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ResolveScenarioPath(string scenarioId)
    {
        var root = ResolveScenarioRoot();
        if (root == null)
        {
            return null;
        }

        var candidate = Path.Combine(root, $"{scenarioId}.json");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolveScenarioRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("HONUA_EVAL_SCENARIO_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot) && Directory.Exists(overrideRoot))
        {
            return overrideRoot;
        }

        var solutionRoot = FindAncestorContaining("Honua.sln");
        if (solutionRoot != null)
        {
            var fromSolution = Path.Combine(solutionRoot, ScenarioRootSegment.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(fromSolution))
            {
                return fromSolution;
            }
        }

        var localRoot = Path.Combine(AppContext.BaseDirectory, ScenarioRootSegment.Replace('/', Path.DirectorySeparatorChar));
        return Directory.Exists(localRoot) ? localRoot : null;
    }

    private static string? FindAncestorContaining(string marker)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, marker)))
        {
            directory = directory.Parent;
        }
        return directory?.FullName;
    }
}
