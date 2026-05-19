// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;

namespace Honua.Server.Features.AiBuilder.Fixtures;

/// <summary>
/// Loads the deterministic AI-builder contract fixtures embedded into Honua.Server
/// from <c>tests/fixtures/ai-builder/*.json</c>. Each fixture pairs a prompt with
/// a draft, plan, and apply envelope; the catalog exposes the parsed scenarios so
/// downstream services (NL planner, intent-to-spec planner, dashboard planner) can
/// replay them without invoking a model.
/// </summary>
/// <remarks>
/// Parsing is performed once at construction using <see cref="JsonDocument"/>; the
/// resulting <see cref="JsonElement"/> values are cloned so callers can walk them
/// independently. Everything here is AOT-safe because we never deserialize the
/// scenarios into a typed model — callers extract the precise sub-trees they need.
/// </remarks>
internal sealed class AiBuilderFixtureCatalog
{
    private const string SpatialQueryResource = "Honua.Server.Features.AiBuilder.Fixtures.spatial-query-contract-v1.json";
    private const string OperationsDashboardResource = "Honua.Server.Features.AiBuilder.Fixtures.operations-dashboard-contract-v1.json";

    private readonly List<AiBuilderScenario> _scenarios;

    public AiBuilderFixtureCatalog()
    {
        var scenarios = new List<AiBuilderScenario>();
        LoadFixture(SpatialQueryResource, AiBuilderFixtureKind.SpatialQuery, scenarios);
        LoadFixture(OperationsDashboardResource, AiBuilderFixtureKind.OperationsDashboard, scenarios);
        _scenarios = scenarios;
    }

    /// <summary>
    /// All loaded scenarios across both contract fixtures.
    /// </summary>
    public IReadOnlyList<AiBuilderScenario> Scenarios => _scenarios;

    /// <summary>
    /// Finds the first scenario whose prompt matches the supplied utterance
    /// after normalization (trim + lowercase). Returns null when no scenario
    /// matches — callers should treat that as "no fixture for this prompt"
    /// rather than an error.
    /// </summary>
    public AiBuilderScenario? FindByPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var key = Normalize(prompt);
        for (var i = 0; i < _scenarios.Count; i++)
        {
            if (string.Equals(Normalize(_scenarios[i].Prompt), key, StringComparison.Ordinal))
            {
                return _scenarios[i];
            }
        }

        return null;
    }

    internal static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static void LoadFixture(string resourceName, AiBuilderFixtureKind kind, List<AiBuilderScenario> sink)
    {
        var assembly = typeof(AiBuilderFixtureCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"AI-builder fixture resource '{resourceName}' is missing from the assembly. "
                + "Confirm the EmbeddedResource entry in Honua.Server.csproj is correct.");

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var contractVersion = root.GetProperty("contractVersion").GetString() ?? string.Empty;

        foreach (var scenario in root.GetProperty("scenarios").EnumerateArray())
        {
            var id = scenario.GetProperty("id").GetString() ?? string.Empty;
            var caseName = scenario.GetProperty("case").GetString() ?? string.Empty;
            var prompt = scenario.GetProperty("prompt").GetString() ?? string.Empty;

            sink.Add(new AiBuilderScenario(
                Kind: kind,
                ContractVersion: contractVersion,
                Id: id,
                Case: caseName,
                Prompt: prompt,
                Root: scenario.Clone()));
        }
    }
}

internal enum AiBuilderFixtureKind
{
    SpatialQuery,
    OperationsDashboard
}

/// <summary>
/// A single fixture scenario. <see cref="Root"/> is the full scenario JSON,
/// cloned from the parsed document so callers can read draft/plan/apply
/// envelopes without worrying about the source <see cref="JsonDocument"/>
/// lifetime.
/// </summary>
internal sealed record AiBuilderScenario(
    AiBuilderFixtureKind Kind,
    string ContractVersion,
    string Id,
    string Case,
    string Prompt,
    JsonElement Root);
