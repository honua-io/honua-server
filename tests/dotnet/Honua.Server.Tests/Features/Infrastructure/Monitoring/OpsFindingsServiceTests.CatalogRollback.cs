// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Alerts;
using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

public sealed partial class OpsFindingsServiceTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_CatalogRollback_RemainsVisibleWithStableAdminChangeEvidence()
    {
        var store = Substitute.For<Honua.Core.Features.Metadata.Abstractions.ICatalogActivationWatchStore>();
        store.ReadRecentRollbacksAsync(default).ReturnsForAnyArgs(new[]
        {
            new Honua.Core.Features.Metadata.Abstractions.CatalogActivationWatch
            {
                Id = 5, Revision = 8, ExpectedEtag = "etag", ActivatedAt = DateTimeOffset.UtcNow,
                RollbackRevision = 9, ChangeId = "catalog-watch-5", Reason = "Sustained serving regression.",
            },
        });
        var service = new OpsFindingsService(
            new StaticOptionsMonitor<OpsFindingsOptions>(new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(new ControlPlaneOptions()),
            new FakeAlertDispatchHealth(), new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            extendedSignals: new OpsFindingsExtendedSignals { CatalogActivationWatches = store });
        var first = (await service.EvaluateAsync()).Single(f => f.Rule == "catalog-activation-rolled-back");
        var second = (await service.EvaluateAsync()).Single(f => f.Rule == "catalog-activation-rolled-back");
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("catalog-watch-5", first.Subject.OperationId);
        Assert.Contains("admin-change:catalog-watch-5", first.EvidenceRefs);
        Assert.Contains("revision 9", first.Explanation);
        Assert.Null(first.RecommendedAction);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Evaluate_CatalogRollbackHistoryUnavailable_PreservesOtherFindingsAndReportsMissingEvidence()
    {
        var store = Substitute.For<Honua.Core.Features.Metadata.Abstractions.ICatalogActivationWatchStore>();
        store.ReadRecentRollbacksAsync(default).ReturnsForAnyArgs(
            Task.FromException<IReadOnlyList<Honua.Core.Features.Metadata.Abstractions.CatalogActivationWatch>>(
                new InvalidOperationException("Catalog history unavailable.")));
        var observer = Substitute.For<IManagedComposeObserver>();
        observer.ReadAsync(Arg.Any<CancellationToken>()).Returns(new OpsManagedTargetHealthView[]
        {
            new() { TargetId = "compose-local", Available = true, Status = "Unhealthy", ObservedAt = DateTimeOffset.UtcNow,
                Services = [new() { Name = "honua", Status = "Unhealthy", DurationMs = 0 }] }
        });
        var service = new OpsFindingsService(
            new StaticOptionsMonitor<OpsFindingsOptions>(new OpsFindingsOptions()),
            new StaticOptionsMonitor<ControlPlaneOptions>(new ControlPlaneOptions()),
            new FakeAlertDispatchHealth(), new FakeDeployPreflightProbe(BuildDeploySnapshot()),
            extendedSignals: new OpsFindingsExtendedSignals
            {
                CatalogActivationWatches = store,
                ManagedComposeObserver = observer
            });

        var evaluation = await service.EvaluateWithEvidenceAsync();

        Assert.Contains(evaluation.Findings, finding => finding.Rule == OpsFindingsService.RuleManagedComposeService);
        var source = Assert.Single(evaluation.Posture.Sources,
            item => item.SourceId == OpsFindingsService.CatalogRollbackSourceId);
        Assert.Equal(EvidencePostureVocabulary.Completeness.Unavailable, source.Completeness);
        Assert.Contains(EvidencePostureVocabulary.ReasonCodes.SourceUnavailable, source.ReasonCodes);
    }
}
