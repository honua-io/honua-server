// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;

namespace Honua.Ai.AiBuilder.Fixtures;

internal sealed class AiBuilderFixtureCatalog
{
    private const string SpatialQueryResource = "Honua.Ai.AiBuilder.Fixtures.spatial-query-contract-v1.json";
    private const string OperationsDashboardResource = "Honua.Ai.AiBuilder.Fixtures.operations-dashboard-contract-v1.json";

    private readonly List<AiBuilderScenario> _scenarios;

    public AiBuilderFixtureCatalog()
    {
        var scenarios = new List<AiBuilderScenario>();
        LoadFixture(SpatialQueryResource, AiBuilderFixtureKind.SpatialQuery, scenarios);
        LoadFixture(OperationsDashboardResource, AiBuilderFixtureKind.OperationsDashboard, scenarios);
        _scenarios = scenarios;
    }

    public IReadOnlyList<AiBuilderScenario> Scenarios => _scenarios;

    public AiBuilderScenario? FindByPrompt(
        string? prompt,
        string? scenarioId = null,
        string? scenarioCase = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var key = Normalize(prompt);
        for (var i = 0; i < _scenarios.Count; i++)
        {
            var scenario = _scenarios[i];
            if (!string.Equals(Normalize(scenario.Prompt), key, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(scenarioId)
                && !string.Equals(scenario.Id, scenarioId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(scenarioCase)
                && !string.Equals(scenario.Case, scenarioCase, StringComparison.Ordinal))
            {
                continue;
            }

            return scenario;
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

internal sealed record AiBuilderScenario(
    AiBuilderFixtureKind Kind,
    string ContractVersion,
    string Id,
    string Case,
    string Prompt,
    JsonElement Root);
