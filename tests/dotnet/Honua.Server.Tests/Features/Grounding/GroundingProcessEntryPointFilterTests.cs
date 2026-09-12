// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Ai.Grounding;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Grounding;

/// <summary>
/// Pins honua-server#4454: a grounded <c>process.selection</c> candidate is what a
/// caller places into an <see cref="AnalysisPlan"/> step and submits to the job
/// runtime, so <see cref="GroundingService"/> must restrict the pool it hands the
/// engine to score to processes <see cref="ProcessExecutionEligibility.IsJobCallable"/>
/// accepts — never a protocol-only or workflow-only operation
/// <see cref="DirectSubmitPlanValidator"/> is guaranteed to refuse. Assertions are
/// pinned against <see cref="ProcessDefinition.SupportedEntryPoints"/> rather than a
/// hard-coded process-id list, so the contract tracks the catalog rather than a
/// snapshot of it.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class GroundingProcessEntryPointFilterTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    [UnitTest]
    public async Task GroundAsync_ScoresOnlyProcessesThatDeclareTheJobEntryPoint()
    {
        var allProcesses = _catalog.ListProcesses();
        allProcesses.Should().Contain(
            p => !ProcessExecutionEligibility.Declares(p, ProcessEntryPoints.Job),
            "the catalog must still own protocol-only/workflow-only operations for this test to be meaningful");

        var engine = Substitute.For<IGroundingEngine>();
        engine.Name.Returns("captor");
        engine.Classify(Arg.Any<GroundingRequest>()).Returns(new WorkflowFamilyClassification
        {
            Value = WorkflowFamily.Analyze,
            Confidence = 0.9
        });
        IReadOnlyList<ProcessDefinition>? scoredPool = null;
        engine
            .ScoreProcesses(Arg.Any<GroundingRequest>(), Arg.Do<IReadOnlyList<ProcessDefinition>>(p => scoredPool = p))
            .Returns([]);

        var processCatalog = Substitute.For<IProcessCatalog>();
        processCatalog.ListProcesses().Returns(allProcesses);
        var authFilter = Substitute.For<IGroundingAuthorizationFilter>();
        authFilter
            .FilterAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyList<GroundingCandidate>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<IReadOnlyList<GroundingCandidate>>(1)));

        var service = new GroundingService(
            engine,
            processCatalog,
            authFilter,
            Options.Create(new GroundingOptions()),
            NullLogger<GroundingService>.Instance,
            serviceScopeFactory: null,
            metadataGraphProvider: new EmptyMetadataV2GraphProvider());

        await service.GroundAsync(
            new GroundingRequest { Goal = "buffer the roads" },
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")));

        scoredPool.Should().NotBeNull();
        scoredPool!.Should().NotBeEmpty();
        var expected = allProcesses.Where(p => ProcessExecutionEligibility.Declares(p, ProcessEntryPoints.Job));
        scoredPool!.Select(p => p.ProcessId).Should().BeEquivalentTo(expected.Select(p => p.ProcessId));
        scoredPool!.Should().OnlyContain(p => ProcessExecutionEligibility.IsJobCallable(p));
    }

    /// <summary>
    /// Uses the real <see cref="DeterministicGroundingEngine"/> and the process's own
    /// title as the goal, which pre-fix scored the process as the top (or only)
    /// candidate because a title self-match is the strongest possible signal. Proves
    /// the exclusion holds through the full ranking pipeline, not just the pool handed
    /// to the engine.
    /// </summary>
    public static TheoryData<string> NonJobCallableProcessIds()
    {
        var data = new TheoryData<string>();
        foreach (var process in new BuiltInProcessCatalog().ListProcesses())
        {
            if (!ProcessExecutionEligibility.Declares(process, ProcessEntryPoints.Job))
            {
                data.Add(process.ProcessId);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NonJobCallableProcessIds))]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task GroundAsync_NeverSurfacesANonJobCallableProcessAsACandidate(string processId)
    {
        var definition = _catalog.GetProcess(processId);
        definition.Should().NotBeNull();

        var service = BuildRealService();
        var request = new GroundingRequest { Goal = definition!.Title };

        var result = await service.GroundAsync(
            request,
            new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")));

        result.Candidates.Processes.Should().NotContain(c => c.Id == processId);

        // Whatever DID rank, it must be a plan the direct-submit runtime accepts on
        // entry-point grounds — the invariant the issue names explicitly.
        foreach (var candidate in result.Candidates.Processes)
        {
            var plan = new AnalysisPlan
            {
                PlanId = "plan-" + candidate.Id,
                IntentId = "intent-" + candidate.Id,
                Steps =
                [
                    new AnalysisPlanStep
                    {
                        StepId = "0",
                        Kind = AnalysisPlanStepKind.Geoprocess,
                        ProcessId = candidate.Id,
                        Inputs = new Dictionary<string, string>()
                    }
                ]
            };

            var (violations, _) = DirectSubmitPlanValidator.Evaluate(plan, _catalog);

            var entryPointViolationCodes = new HashSet<string>(
                ["SYNC_ONLY_PROCESS", "WORKFLOW_ONLY_PROCESS", "PROCESS_UNAVAILABLE", "UNCLASSIFIED_PROCESS"],
                StringComparer.Ordinal);
            violations.Should().NotContain(v => entryPointViolationCodes.Contains(v.Code));
        }
    }

    private static GroundingService BuildRealService()
    {
        var engine = new DeterministicGroundingEngine();
        var catalog = new BuiltInProcessCatalog();
        var authFilter = Substitute.For<IGroundingAuthorizationFilter>();
        authFilter
            .FilterAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<IReadOnlyList<GroundingCandidate>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<IReadOnlyList<GroundingCandidate>>(1)));

        return new GroundingService(
            engine,
            catalog,
            authFilter,
            Options.Create(new GroundingOptions()),
            NullLogger<GroundingService>.Instance,
            serviceScopeFactory: null,
            metadataGraphProvider: new EmptyMetadataV2GraphProvider());
    }

    private sealed class EmptyMetadataV2GraphProvider : IMetadataV2GraphProvider
    {
        private static readonly MetadataV2GraphSnapshot Snapshot = new(
            new MetadataV2Graph(),
            "\"test\"",
            DateTimeOffset.UtcNow);

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => new(Snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            long revision,
            CancellationToken cancellationToken = default)
            => new((MetadataV2GraphSnapshot?)null);
    }
}
