// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Eval;

internal static class EvalHarnessSupport
{
    public static string? ResolveSeedProfile(IReadOnlyList<EvalScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        var profiles = scenarios
            .Select(scenario => scenario.FixtureProfile)
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return profiles.Length switch
        {
            0 => null,
            1 => profiles[0],
            _ => throw new EvalScenarioException(
                "Eval harness uses one class-scoped seeded schema per run and therefore " +
                "requires a single fixture profile. " +
                $"Found multiple profiles: {string.Join(", ", profiles)}.")
        };
    }

    public static bool DetermineRedisAvailability(IReadOnlyList<EvalScenarioResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        return results.Any(result => result.Stages.Any(stage =>
            stage.Stage == EvalStageKind.SubmitPlanJob &&
            stage.Status == EvalStageStatus.Passed));
    }
}
