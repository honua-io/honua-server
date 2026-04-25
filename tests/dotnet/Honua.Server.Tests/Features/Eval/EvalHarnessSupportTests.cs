// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Eval;
using Honua.TestKit.Seeding;
using Xunit;

namespace Honua.Server.Tests.Features.Eval;

[Protocol(TestProtocols.OperatorEval)]
public sealed class EvalHarnessSupportTests
{
    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void ResolveSeedProfile_WithMixedScenarioProfiles_Throws()
    {
        var scenarios = new[]
        {
            new EvalScenario { Id = "core", FixtureProfile = "core" },
            new EvalScenario { Id = "ogc", FixtureProfile = "ogc" }
        };

        var act = () => EvalHarnessSupport.ResolveSeedProfile(scenarios);

        act.Should().Throw<EvalScenarioException>()
            .WithMessage("*single fixture profile*");
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void DetermineRedisAvailability_WhenSubmitStagePassed_ReturnsTrue()
    {
        var results = new[]
        {
            new EvalScenarioResult
            {
                Stages =
                [
                    new EvalStageOutcome
                    {
                        Stage = EvalStageKind.SubmitPlanJob,
                        Status = EvalStageStatus.Passed
                    }
                ]
            }
        };

        EvalHarnessSupport.DetermineRedisAvailability(results).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void DetermineRedisAvailability_WhenSubmitStageSkippedForRedisUnavailable_ReturnsFalse()
    {
        var results = new[]
        {
            new EvalScenarioResult
            {
                Stages =
                [
                    new EvalStageOutcome
                    {
                        Stage = EvalStageKind.SubmitPlanJob,
                        Status = EvalStageStatus.Skipped,
                        Reason = "redis-unavailable"
                    }
                ]
            }
        };

        EvalHarnessSupport.DetermineRedisAvailability(results).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void SharedCorpusFixtureSource_TryCreate_WithSeedDirectory_ResolvesSeedPath()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var seedPath = Path.Combine(tempDirectory.FullName, "seed.yaml");
            File.WriteAllText(seedPath, "version: 1\ncollections: []\nfeatures: []\n");

            var source = SharedCorpusFixtureSource.TryCreate(tempDirectory.FullName, "corpus@123");

            source.Should().NotBeNull();
            source!.Id.Should().Be("shared");
            source.CorpusPath.Should().Be(tempDirectory.FullName);
            source.CorpusVersion.Should().Be("corpus@123");
            source.SeedPath.Should().Be(seedPath);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void SharedCorpusFixtureSource_TryCreate_WithMissingSeed_Throws()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var act = () => SharedCorpusFixtureSource.TryCreate(tempDirectory.FullName, "corpus@123");

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*must point to a YAML seed file or a directory containing seed.yaml*");
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(BundledScenarioIds))]
    [Operation(Operations.ContractTesting)]
    [Trait("Category", "Unit")]
    public void BundledScenario_DeclaredInputsAreServedByItsFixtureProfile(string scenarioId)
    {
        var scenario = EvalScenarioLoader.LoadById(scenarioId);
        var seedPath = ResolveRepoRelativePath(Path.Combine("tests", "seed", "seed.yaml"));

        File.Exists(seedPath).Should().BeTrue(
            because: $"seed corpus must be discoverable at '{seedPath}' for eval harness validation");

        var allowed = SeedRunner.GetAllowedCollections(seedPath, scenario.FixtureProfile);

        allowed.Should().NotBeNull(
            because: $"scenario '{scenarioId}' declares fixtureProfile '{scenario.FixtureProfile}' " +
                     "which must exist in the seed corpus");

        var missing = scenario.Intent.Inputs
            .Where(input => !allowed!.Contains(input, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        missing.Should().BeEmpty(
            because: $"scenario '{scenarioId}' declares inputs [{string.Join(", ", scenario.Intent.Inputs)}] " +
                     $"but fixtureProfile '{scenario.FixtureProfile}' only seeds " +
                     $"[{string.Join(", ", allowed!)}]. Either change fixtureProfile or expand the profile's " +
                     "collections in tests/seed/seed.yaml so CI provisions the data the scenario claims to exercise.");
    }

    public static IEnumerable<object[]> BundledScenarioIds()
    {
        foreach (var id in EvalScenarioLoader.DiscoverScenarioIds())
        {
            yield return [id];
        }
    }

    private static string ResolveRepoRelativePath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory != null
            ? Path.Combine(directory.FullName, relative)
            : Path.Combine(AppContext.BaseDirectory, relative);
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    public void EvalScenarioLoader_WithInvalidScenarioRootOverride_FailsClosed()
    {
        const string OverrideVariable = "HONUA_EVAL_SCENARIO_ROOT";
        var original = Environment.GetEnvironmentVariable(OverrideVariable);
        var missingDirectory = Path.Combine(Path.GetTempPath(), "honua-eval-missing-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(OverrideVariable, missingDirectory);

            var loadAct = () => EvalScenarioLoader.LoadById("analysis-buffer-places");
            var discoverAct = () => EvalScenarioLoader.DiscoverScenarioIds();

            loadAct.Should().Throw<EvalScenarioException>()
                .WithMessage($"*{missingDirectory}*directory does not exist*");
            discoverAct.Should().Throw<EvalScenarioException>()
                .WithMessage($"*{missingDirectory}*directory does not exist*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(OverrideVariable, original);
        }
    }
}
