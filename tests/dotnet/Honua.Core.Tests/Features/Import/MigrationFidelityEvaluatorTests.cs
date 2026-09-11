// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Issue #4600, acceptance criterion 3: data reconciliation, catalog reconciliation, record loss,
/// attachment loss and relationship omissions must fold into one migration verdict. Before this
/// evaluator the data artifact alone decided Completed vs NeedsReview while catalog findings — the
/// pass that catches "counts match but the schema is wrong" — were recorded and then ignored.
/// </summary>
/// <remarks>
/// Every expected verdict below is derived from the acceptance criterion, not from a snapshot of
/// the evaluator's output: a blocking difference must yield <c>incomplete</c>, an unexecuted
/// required check must yield <c>unverified</c> (never <c>full-fidelity</c>), and only an
/// all-checks-ran-and-passed run may report <c>full-fidelity</c>.
/// </remarks>
public sealed class MigrationFidelityEvaluatorTests
{
    [Fact]
    public void Evaluate_WhenEveryCheckRanAndPassed_ReportsFullFidelity()
    {
        var evaluation = MigrationFidelityEvaluator.Evaluate(FullyVerifiedInput());

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.FullFidelity);
        evaluation.Differences.Should().BeEmpty();
        evaluation.IsBlocking.Should().BeFalse();
        evaluation.BlockingReason.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WhenCatalogReconciliationFails_BlocksEvenThoughDataReconciliationPassed()
    {
        // The exact shape #4600 calls out: the data-movement pass is green (every record moved)
        // while the catalog pass found a dropped domain. The old gate keyed only on the data
        // artifact and would have reported Completed.
        var input = FullyVerifiedInput() with
        {
            CatalogReconciliation = CatalogReport(
                "resource:Inspections:layer:0",
                MigrationCatalogReconciliationClassifications.Fail,
                new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.DomainMissing,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = "Status",
                    Expected = "StatusDomain",
                    Actual = "(none)",
                    Summary = "Field 'Status' carried a coded-value domain that is absent from the published resource."
                })
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);
        evaluation.IsBlocking.Should().BeTrue();

        var difference = evaluation.Differences
            .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.CatalogReconciliationFailed)
            .Subject;
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
        difference.Subject.Should().Be("resource:Inspections:layer:0:Status");
        difference.Expected.Should().Be("StatusDomain");
        difference.Actual.Should().Be("(none)");
        evaluation.BlockingReason.Should().Contain(MigrationCatalogReconciliationCodes.DomainMissing);
    }

    [Fact]
    public void Evaluate_WhenCatalogFindingIsWarnOnly_DoesNotBlock()
    {
        // Warn findings surface to operators without halting the import; only fail-severity
        // findings are parity blockers.
        var input = FullyVerifiedInput() with
        {
            CatalogReconciliation = CatalogReport(
                "resource:Inspections:layer:0",
                MigrationCatalogReconciliationClassifications.Warn,
                new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.ExtentMismatch,
                    Severity = MigrationCatalogReconciliationSeverities.Warn,
                    Subject = "spatial",
                    Summary = "Published extent is wider than the declared source extent."
                })
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.FullFidelity);
        evaluation.Differences.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_WhenRecordsWereDropped_BlocksWithTheDroppedCount()
    {
        var evaluation = MigrationFidelityEvaluator.Evaluate(FullyVerifiedInput() with { FailedFeatures = 4 });

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);

        var difference = evaluation.Differences
            .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.RecordsLost)
            .Subject;
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Blocking);
        difference.Subject.Should().Be("Inspections");
        difference.Expected.Should().Be("0 dropped source records");
        difference.Actual.Should().Be("4 dropped source records");
    }

    [Fact]
    public void Evaluate_WhenAttachmentsAreLost_BlocksIndependentlyOfMatchingFeatureCounts()
    {
        // AC6: attachments are validated on their own evidence. Feature counts are untouched here
        // (FailedFeatures stays 0 and the data artifact is green), so only the attachment inventory
        // can catch the loss.
        var input = FullyVerifiedInput() with
        {
            Attachments = new MigrationFidelityAttachmentInput
            {
                Advertised = 10,
                Copied = 7,
                Failed = 3
            }
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);

        var difference = evaluation.Differences
            .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.AttachmentsLost)
            .Subject;
        difference.Subject.Should().Be("attachments");
        difference.Expected.Should().Be("10 attachments in the target store");
        difference.Actual.Should().Be("7 attachments in the target store");
    }

    [Fact]
    public void Evaluate_WhenAdvertisedAttachmentsExceedCopiedWithoutFailures_StillBlocks()
    {
        // Neither a success nor a failure was recorded for 2 of the 5 advertised attachments. A
        // shortfall with no explanation is still a shortfall.
        var input = FullyVerifiedInput() with
        {
            Attachments = new MigrationFidelityAttachmentInput
            {
                Advertised = 5,
                Copied = 3,
                Failed = 0
            }
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);
        evaluation.Differences.Should()
            .ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.AttachmentsLost);
    }

    [Fact]
    public void Evaluate_WhenAttachmentInventoryCouldNotBeRead_DowngradesToUnverifiedWithoutBlocking()
    {
        // An unreadable inventory is not proof of loss, but it is equally not proof of parity.
        var input = FullyVerifiedInput() with
        {
            Attachments = new MigrationFidelityAttachmentInput
            {
                Advertised = 4,
                Copied = 4,
                Failed = 0,
                UnverifiedParents = 12
            }
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        evaluation.IsBlocking.Should().BeFalse();

        var difference = evaluation.Differences
            .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.AttachmentsUnverified)
            .Subject;
        difference.Severity.Should().Be(MigrationFidelityDifferenceSeverities.Unverified);
        difference.Actual.Should().Be("12 features with an unreadable attachment inventory");
    }

    [Fact]
    public void Evaluate_WhenAttachmentCopyWasSkipped_IsUnverifiedRatherThanFullFidelity()
    {
        var input = FullyVerifiedInput() with
        {
            Attachments = new MigrationFidelityAttachmentInput { CopySkipped = true }
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        evaluation.Differences.Should()
            .ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.AttachmentsUnverified);
    }

    [Fact]
    public void Evaluate_WhenARelationshipWasDeferred_BlocksRatherThanOmittingSilently()
    {
        // AC2: never a silent omission. A deferred relationship and an idempotent re-apply both
        // report AlreadyExists, so the evaluator must key on the explicit Deferred flag.
        var input = FullyVerifiedInput() with
        {
            Relationships =
            [
                new MigrationRelationshipApplyOutcome
                {
                    SourceRelationshipId = "rel:Inspections:2",
                    Outcome = MigrationCatalogWriteOutcome.AlreadyExists,
                    Message = "Many-to-many relationships require a junction table.",
                    TargetRelationshipRef = "rel-11-2",
                    Deferred = true
                },
                new MigrationRelationshipApplyOutcome
                {
                    SourceRelationshipId = "rel:Inspections:1",
                    Outcome = MigrationCatalogWriteOutcome.AlreadyExists,
                    Message = "Relationship already present on the target; no mutation performed.",
                    TargetRelationshipRef = "rel-11-1"
                }
            ]
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);

        // Only the deferred relationship is an omission; the idempotent re-apply is not.
        var difference = evaluation.Differences
            .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.RelationshipOmitted)
            .Subject;
        difference.Subject.Should().Be("rel:Inspections:2");
    }

    [Fact]
    public void Evaluate_WhenCatalogCheckNeverExecuted_IsUnverifiedNotFullFidelity()
    {
        // "Counts can match while the schema is wrong": a run that never read the published
        // catalog entry back has no evidence of schema parity and must not claim full fidelity.
        var input = FullyVerifiedInput() with
        {
            CatalogReconciliation = null,
            CatalogReconciliationExecuted = false
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        evaluation.IsBlocking.Should().BeFalse();
        evaluation.Differences.Should()
            .ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.CatalogReconciliationNotExecuted);
    }

    [Fact]
    public void Evaluate_WhenDataCheckNeverExecuted_IsUnverifiedNotFullFidelity()
    {
        var input = FullyVerifiedInput() with
        {
            DataReconciliation = null,
            DataReconciliationExecuted = false
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        evaluation.Differences.Should()
            .ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.DataReconciliationNotExecuted);
    }

    [Fact]
    public void Evaluate_WhenNothingWasPublished_DoesNotInventNotExecutedDifferencesForPostPublishProbes()
    {
        // With no published target there is nothing for the post-publish probes to reconcile
        // against, so they are not applicable rather than skipped.
        var input = new MigrationFidelityEvaluationInput
        {
            LayerName = "Inspections",
            PublishedTarget = false
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.FullFidelity);
        evaluation.Differences.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_WhenDataReconciliationFails_BlocksAndCarriesItsReasons()
    {
        var input = FullyVerifiedInput() with
        {
            DataReconciliation = DataArtifact(
                MigrationReconciliationClassifications.Fail,
                failCount: 2,
                reasons: ["Target is missing 31 of 900 source features."])
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);

        var difference = evaluation.Differences
            .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.DataReconciliationFailed)
            .Subject;
        difference.Expected.Should().Be(MigrationReconciliationClassifications.Pass);
        difference.Actual.Should().Be(MigrationReconciliationClassifications.Fail);
        difference.Summary.Should().Contain("2 blocking finding(s)");
        difference.Summary.Should().Contain("31 of 900");
    }

    [Fact]
    public void Evaluate_WhenDataReconciliationWasSkipped_IsUnverifiedNotFullFidelity()
    {
        var input = FullyVerifiedInput() with
        {
            DataReconciliation = DataArtifact(MigrationReconciliationClassifications.Skipped, failCount: 0, reasons: [])
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);
        evaluation.Differences.Should()
            .ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.DataReconciliationNotExecuted);
    }

    [Fact]
    public void Evaluate_WhenAResourceHadNoPublishedCatalogEntry_IsUnverified()
    {
        var input = FullyVerifiedInput() with
        {
            CatalogReconciliation = CatalogReport(
                "resource:Inspections:table:3",
                MigrationCatalogReconciliationClassifications.NotApplicable)
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Unverified);

        var difference = evaluation.Differences
            .Should().ContainSingle(d => d.Code == MigrationFidelityDifferenceCodes.CatalogReconciliationNotExecuted)
            .Subject;
        difference.Subject.Should().Be("resource:Inspections:table:3");
    }

    [Fact]
    public void Evaluate_WhenBlockingAndUnverifiedDifferencesCoexist_BlockingWins()
    {
        // A run that both lost data and skipped a check is incomplete, not merely unverified.
        var input = FullyVerifiedInput() with
        {
            FailedFeatures = 1,
            CatalogReconciliation = null,
            CatalogReconciliationExecuted = false
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Verdict.Should().Be(MigrationFidelityVerdicts.Incomplete);
        evaluation.Differences.Should().HaveCount(2);
        evaluation.BlockingReason.Should().StartWith("Migration is not full fidelity: 1 blocking difference(s).");
    }

    [Fact]
    public void Evaluate_OrdersDifferencesByCodeThenSubject()
    {
        // Persisted evidence and operator tooling key off this ordering, so it is part of the
        // contract rather than an implementation detail.
        var input = FullyVerifiedInput() with
        {
            FailedFeatures = 1,
            Attachments = new MigrationFidelityAttachmentInput { Advertised = 2, Copied = 1, Failed = 1 },
            CatalogReconciliation = CatalogReport(
                "resource:b",
                MigrationCatalogReconciliationClassifications.Fail,
                new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.FieldMissing,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = "zulu",
                    Summary = "Field 'zulu' is missing."
                },
                new MigrationCatalogReconciliationFinding
                {
                    Code = MigrationCatalogReconciliationCodes.FieldMissing,
                    Severity = MigrationCatalogReconciliationSeverities.Fail,
                    Subject = "alpha",
                    Summary = "Field 'alpha' is missing."
                })
        };

        var evaluation = MigrationFidelityEvaluator.Evaluate(input);

        evaluation.Differences.Select(d => (d.Code, d.Subject)).Should().Equal(
        [
            (MigrationFidelityDifferenceCodes.AttachmentsLost, "attachments"),
            (MigrationFidelityDifferenceCodes.CatalogReconciliationFailed, "resource:b:alpha"),
            (MigrationFidelityDifferenceCodes.CatalogReconciliationFailed, "resource:b:zulu"),
            (MigrationFidelityDifferenceCodes.RecordsLost, "Inspections")
        ]);
    }

    [Fact]
    public void Evaluate_IsDeterministic()
    {
        var input = FullyVerifiedInput() with
        {
            FailedFeatures = 2,
            Attachments = new MigrationFidelityAttachmentInput { Advertised = 3, Copied = 2, Failed = 1 }
        };

        var first = MigrationFidelityEvaluator.Evaluate(input);
        var second = MigrationFidelityEvaluator.Evaluate(input);

        second.Verdict.Should().Be(first.Verdict);
        second.BlockingReason.Should().Be(first.BlockingReason);
        second.Differences.Should().Equal(first.Differences);
    }

    [Fact]
    public void Evaluate_WithNullInput_Throws()
    {
        var act = () => MigrationFidelityEvaluator.Evaluate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// A run where every required check executed and reported parity, with no data, attachment or
    /// relationship loss. The only input shape that may be reported as a full-fidelity migration.
    /// </summary>
    private static MigrationFidelityEvaluationInput FullyVerifiedInput() => new()
    {
        LayerName = "Inspections",
        PublishedTarget = true,
        FailedFeatures = 0,
        DataReconciliation = DataArtifact(MigrationReconciliationClassifications.Pass, failCount: 0, reasons: []),
        DataReconciliationExecuted = true,
        CatalogReconciliation = CatalogReport(
            "resource:Inspections:layer:0",
            MigrationCatalogReconciliationClassifications.Pass),
        CatalogReconciliationExecuted = true
    };

    private static MigrationReconciliationArtifact DataArtifact(
        string classification,
        int failCount,
        string[] reasons) => new()
        {
            RunId = "run-4600",
            SourceKind = "arcgis-geoservices-rest",
            Classification = classification,
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch,
            Summary = new MigrationReconciliationSummary { LayerCount = 1, FailCount = failCount },
            Layers = [],
            Reasons = reasons,
            Options = new LayerReconciliationOptions()
        };

    private static MigrationCatalogReconciliationReport CatalogReport(
        string sourceResourceId,
        string classification,
        params MigrationCatalogReconciliationFinding[] findings) => new()
        {
            RunId = "run-4600",
            SourceKind = "arcgis-geoservices-rest",
            Summary = new MigrationCatalogReconciliationSummary
            {
                ResourceCount = 1,
                FindingCount = findings.Length
            },
            Resources =
            [
                new MigrationCatalogReconciliationResource
                {
                    SourceResourceId = sourceResourceId,
                    Classification = classification,
                    Findings = findings
                }
            ]
        };
}
