from pathlib import Path
base=Path('tests/dotnet/Honua.Server.Tests/Features/Infrastructure/ControlPlane')
p=base/'JobExecutionContextPublishFencingTests.cs';s=p.read_text();marker='    [UnitTest]\n    public async Task PublishArtifact_StaleAttempt_IsFenced()'
s=s.replace(marker,'''    [UnitTest]
    public async Task RecordCommittedEffect_CancellationAndCasConflict_RetainsReceiptIdempotently()
    {
        var durable = CreateRunningJob(attemptCount: 1) with { CancellationRequestedAt = DateTimeOffset.UtcNow };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        var writes = 0;
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (++writes == 1)
                {
                    durable = durable with { Version = durable.Version + 1, ArtifactReferences = ["earlier-output"] };
                    return false;
                }
                durable = call.Arg<ExecutionJobRecord>();
                return true;
            });
        using var context = CreateContext(durable.OperationId, store, claimedAttempt: 1);

        await context.RecordCommittedEffectAsync("committed-receipt", CancellationToken.None);
        await context.RecordCommittedEffectAsync("committed-receipt", CancellationToken.None);

        durable.ArtifactReferences.Should().Equal("earlier-output", "committed-receipt");
        durable.CommittedEffectReferences.Should().Equal("committed-receipt");
        writes.Should().Be(2);
        (await context.TryPublishArtifactAsync("later-output", CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Tier", "Fast")]
    public async Task RecordCommittedEffect_StaleOwnershipOrAttempt_IsRejectedExplicitly(bool staleAttempt)
    {
        var durable = CreateRunningJob(attemptCount: staleAttempt ? 2 : 1) with
        {
            ClaimedBy = staleAttempt ? WorkerId : "replacement-worker",
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var store = Substitute.For<IExecutionJobStore>().WithTrySet();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(durable);
        using var context = CreateContext(durable.OperationId, store, claimedAttempt: 1);

        await FluentActions.Awaiting(() => context.RecordCommittedEffectAsync("committed-receipt"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*fence rejected*committed-effect receipt*");
        await store.DidNotReceive().TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

'''+marker);assert s!=p.read_text();p.write_text(s)
p=base/'JobExecutionServiceTests.cs';s=p.read_text();marker='    [Theory]\n    [InlineData(false)]';idx=s.index(marker);s=s[:idx]+'''    [UnitTest]
    public async Task ProcessJob_OrdinaryCompletionAfterEarlierCommittedEffect_HonoursCancellationAndReturnsOnlyReceipt()
    {
        var durable = CreateProvisioningJob();
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(durable.OperationId, Arg.Any<CancellationToken>()).Returns(_ => durable);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var context = call.Arg<IJobExecutionContext>();
                await context.RecordCommittedEffectAsync("earlier-committed-receipt");
                durable = durable with { CancellationRequestedAt = DateTimeOffset.UtcNow };
                Assert.False(await context.TryPublishArtifactAsync("ordinary-later-output"));
                return JobExecutionResult.Succeeded();
            });
        using var service = new JobExecutionService(Substitute.For<IJobQueue>(), store, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);

        await InvokeProcessJobAsync(service, durable.OperationId, durable.ClaimedBy!);

        Assert.Equal(ExecutionJobStatus.Cancelled, durable.Status);
        Assert.Equal("Cancelled after committed effects", durable.CurrentPhase);
        Assert.Equal(new[] { "earlier-committed-receipt" }, durable.ArtifactReferences);
        Assert.Contains(durable.Warnings, warning => warning.Contains("committed", StringComparison.Ordinal));
    }

'''+s[idx:];p.write_text(s)
# Existing sink expectations continue to require the same publication contents, now on the committed receipt channel.
p=Path('tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/HonuaLayerSinkExecutorTests.cs');s=p.read_text();marker='''        var record = CreateRecord(inputs);''';s=s.replace(marker,'''        context.When(c => c.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.Arg<string>());

'''+marker,1);p.write_text(s)
p=Path('tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/ExternalPostgisSinkExecutorTests.cs');s=p.read_text();marker='''        var input = BuildInputUri(new Feature(''';s=s.replace(marker,'''        context.When(c => c.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                cancellation.Cancel();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                receipt = call.Arg<string>();
            });
'''+marker,1)
marker='''        var record = Record(
            ("input", input),''';s=s.replace(marker,'''        context.When(c => c.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.Arg<string>());

'''+marker,1);p.write_text(s)
p=Path('tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/GeoprocessingJobServiceTests.cs');s=p.read_text();marker='    [Theory]\n    [InlineData("create")]';assert marker in s
s=s.replace(marker,'''    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Create)]
    public async Task SubmitJob_CancellationRacingQueueClaim_PreservesAcceptedAttemptForReplay(bool requeued)
    {
        using var request = new CancellationTokenSource();
        ExecutionJobRecord? durable = null;
        _jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => durable);
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        _jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { durable = call.Arg<ExecutionJobRecord>(); return true; });
        _jobQueue.EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // Delivery and its worker claim committed before the request observed cancellation.
                var accepted = durable!;
                durable = accepted with
                {
                    Status = requeued ? ExecutionJobStatus.Queued : ExecutionJobStatus.Provisioning,
                    ClaimedBy = requeued ? null : "accepted-worker",
                    AttemptCount = 1,
                    Version = accepted.Version + 1
                };
                request.Cancel();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await FluentActions.Awaiting(() => _sut.SubmitJobAsync(
                CreateValidPlan(), "claimed-admission", CreatePrincipal(), cancellationToken: request.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        durable!.Status.Should().Be(requeued ? ExecutionJobStatus.Queued : ExecutionJobStatus.Provisioning);
        durable.ClaimedBy.Should().Be(requeued ? null : "accepted-worker");
        var replay = await _sut.SubmitJobAsync(CreateValidPlan(), "claimed-admission", CreatePrincipal());
        replay.Should().BeSameAs(durable);
        replay.AttemptCount.Should().Be(1);
        await _jobQueue.Received(1).EnqueueAsync(replay.OperationId, replay.Priority, Arg.Any<CancellationToken>());
        await _jobStore.DidNotReceive().TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

'''+marker,1);p.write_text(s)
p=Path('tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/LayerSinkExecutionProofTests.cs');s=p.read_text();marker='        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(c => artifacts.Add(c.ArgAt<string>(0)));';assert marker in s;s=s.replace(marker,marker+'\n        context.When(c => c.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(c => artifacts.Add(c.ArgAt<string>(0)));',1);p.write_text(s)
