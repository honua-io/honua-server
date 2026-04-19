// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Eval;
using Xunit;

namespace Honua.Server.Tests.Features.Eval;

[Protocol(Protocols.OperatorEval)]
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
