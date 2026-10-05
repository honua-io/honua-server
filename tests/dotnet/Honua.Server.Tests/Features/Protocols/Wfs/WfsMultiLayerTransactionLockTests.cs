// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Collaboration;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Wfs;

[Protocol(TestProtocols.Wfs20)]
public sealed class WfsMultiLayerTransactionLockTests
{
    [UnitTheory]
    [InlineData(FeatureWriterTransactionCommitOutcome.Committed)]
    [InlineData(FeatureWriterTransactionCommitOutcome.Unknown)]
    [Operation(Operations.Update)]
    public async Task WriterTransaction_PreservesProviderCompletion(FeatureWriterTransactionCommitOutcome outcome)
    {
        var provider = Substitute.For<IFeatureWriter>();
        var inner = Substitute.For<IFeatureWriterTransaction>();
        using var cancellation = new CancellationTokenSource();
        provider.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation.Token).Returns(Task.FromResult(inner));
        inner.CommitAsync(cancellation.Token).Returns(Task.FromResult(outcome));
        var locks = new InMemoryFeatureLockService();
        var writer = new FeatureLockEnforcingFeatureWriter(provider, locks, new FeatureEditGuard(locks),
            Substitute.For<IMetadataV2GraphProvider>(), new HttpContextAccessor());

        await using (var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation.Token))
        {
            (await transaction.CommitAsync(cancellation.Token)).Should().Be(outcome);
        }

        await provider.Received(1).BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellation.Token);
        await inner.Received(1).CommitAsync(cancellation.Token);
        await inner.Received(1).DisposeAsync();
        await inner.DidNotReceive().RollbackAsync(Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("Update")]
    [InlineData("Delete")]
    [Operation(Operations.Update)]
    public async Task Transaction_LeasedFeatureInSecondLayer_MatchesSingleLayerRefusal(string action)
    {
        using var scenario = new TransactionScenario();
        await scenario.ClaimSecondLayerAsync();

        var single = await scenario.ExecuteAsync(action, multiLayer: false);
        single.Status.Should().Be(StatusCodes.Status400BadRequest, single.Body);
        single.Body.Should().Contain("OperationProcessingFailed").And.Contain("Alice Editor");
        scenario.OrdinaryApplyCalls.Should().Be(0);

        var multiple = await scenario.ExecuteAsync(action, multiLayer: true);
        multiple.Status.Should().Be(single.Status, multiple.Body);
        multiple.Body.Should().Contain("OperationProcessingFailed").And.Contain("Alice Editor");
        scenario.AppliedLayers.Should().Equal(1);
        scenario.RollbackCalls.Should().Be(1);
        scenario.CommitCalls.Should().Be(0);
        scenario.DisposeCalls.Should().Be(1);
        scenario.Features.Keys.Should().BeEquivalentTo([1, 2]);
        scenario.Features.Values.Should().OnlyContain(feature => (string)feature.Attributes["name"]! == "original");
        if (action == "Delete")
        {
            scenario.AuditEvents.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Failure);
        }
    }

    [UnitTheory]
    [InlineData("Update", false)]
    [InlineData("Delete", false)]
    [InlineData("Update", true)]
    [InlineData("Delete", true)]
    [Operation(Operations.Update)]
    public async Task Transaction_UnleasedOrLeaseHolder_CommitsBothLayers(string action, bool leaseHolder)
    {
        using var scenario = new TransactionScenario();
        if (leaseHolder)
        {
            await scenario.ClaimSecondLayerAsync();
        }

        var response = await scenario.ExecuteAsync(action, multiLayer: true, holder: leaseHolder ? "alice" : "bob");

        response.Status.Should().Be(StatusCodes.Status200OK, response.Body);
        response.Body.Should().Contain("TransactionResponse");
        scenario.AppliedLayers.Should().Equal(1, 2);
        scenario.CommitCalls.Should().Be(1);
        scenario.RollbackCalls.Should().Be(0);
        scenario.DisposeCalls.Should().Be(1);
        scenario.OrdinaryApplyCalls.Should().Be(0);
        scenario.IsolationLevel.Should().Be(IsolationLevel.Serializable);
        if (action == "Delete")
        {
            scenario.Features.Should().BeEmpty();
            scenario.AuditEvents.Should().HaveCount(2).And.OnlyContain(audit => audit.Outcome == AuditOutcome.Success);
        }
        else
        {
            scenario.Features.Values.Should().OnlyContain(feature => (string)feature.Attributes["name"]! == "changed");
        }
    }

    private sealed class TransactionScenario : IDisposable
    {
        private readonly InMemoryFeatureLockService _locks = new();
        private readonly HttpContextAccessor _accessor = new();
        private readonly ServiceProvider _services;
        private readonly Wfs20Handler _handler;
        private readonly Dictionary<int, Feature> _pending = [];

        public Dictionary<int, Feature> Features { get; } = new()
        {
            [1] = FeatureWithId(11),
            [2] = FeatureWithId(22)
        };

        public List<int> AppliedLayers { get; } = [];
        public List<AuditEvent> AuditEvents { get; } = [];
        public int OrdinaryApplyCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public IsolationLevel IsolationLevel { get; private set; }

        public TransactionScenario()
        {
            var policy = new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = true };
            var builder = new TestMetadataV2GraphBuilder()
                .AddService("svc", "parcels", protocols: [ServiceProtocols.Wfs20], accessPolicy: policy);
            for (var layer = 1; layer <= 2; layer++)
            {
                builder.AddResource($"res-{layer}", $"layer{layer}", fields:
                    [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }], accessPolicy: policy)
                    .AddStorageBinding($"binding-{layer}", $"res-{layer}", $"public.layer{layer}", storageLayerId: layer)
                    .AddPublication($"pub-{layer}", "svc", $"res-{layer}", layerIndex: layer, storageBindingId: $"binding-{layer}");
            }

            IMetadataV2GraphProvider metadata = builder.BuildProvider();
            _services = new ServiceCollection().AddLogging()
                .AddSingleton(metadata)
                .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
                .BuildServiceProvider();

            var provider = Substitute.For<IFeatureWriter>();
            var transaction = Substitute.For<IFeatureWriterTransaction>();
            provider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                IsolationLevel = call.Arg<IsolationLevel>();
                foreach (var pair in Features) _pending.Add(pair.Key, pair.Value);
                return Task.FromResult(transaction);
            });
            provider.ApplyEditsAsync(Arg.Any<int>(), Arg.Any<FeatureEditBatch>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                OrdinaryApplyCalls++;
                return Task.FromResult(Apply(Features, call.Arg<int>(), call.Arg<FeatureEditBatch>()));
            });
            transaction.ApplyEditsAsync(Arg.Any<int>(), Arg.Any<FeatureEditBatch>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                AppliedLayers.Add(call.Arg<int>());
                return Task.FromResult(Apply(_pending, call.Arg<int>(), call.Arg<FeatureEditBatch>()));
            });
            transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                CommitCalls++;
                Features.Clear();
                foreach (var pair in _pending) Features.Add(pair.Key, pair.Value);
                return Task.FromResult(FeatureWriterTransactionCommitOutcome.Committed);
            });
            transaction.RollbackAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                RollbackCalls++;
                _pending.Clear();
                return Task.CompletedTask;
            });
            transaction.DisposeAsync().Returns(_ =>
            {
                DisposeCalls++;
                _pending.Clear();
                return ValueTask.CompletedTask;
            });

            var auditLog = Substitute.For<IAuditLog>();
            auditLog.RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                AuditEvents.Add(call.Arg<AuditEvent>());
                return Task.FromResult<string?>(null);
            });
            var writer = new FeatureLockEnforcingFeatureWriter(
                new AuditingFeatureWriter(provider, auditLog, _accessor),
                _locks, new FeatureEditGuard(_locks), metadata, _accessor);
            var reader = Substitute.For<IFeatureReader>();
            reader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<Feature?>(Features.GetValueOrDefault(call.Arg<int>())));
            reader.QueryObjectIdsAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(ImmutableArray.Create(Features[call.Arg<int>()].Id)));
            var coordinateTransform = Substitute.For<ICoordinateTransformService>();
            var limits = Options.Create(new LimitsOptions());
            _handler = new Wfs20Handler(NullLogger<Wfs20Handler>.Instance,
                new Wfs20QueryServices(reader, Substitute.For<IGmlFeatureStore>(), metadata,
                    Substitute.For<IFilterExpressionService>(),
                    new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
                    new QueryProcessor(Substitute.For<IFilterExpressionTranslator>(), reader, NullLogger<QueryProcessor>.Instance, metadata),
                    Options.Create(new Wfs20Options()), new ConfigurationBuilder().Build()),
                new Wfs20EditServices(writer, new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance),
                    new EditProcessor(NullLogger<EditProcessor>.Instance),
                    new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
                    new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()), limits),
                new Wfs20SpatialServices(new OgcFeaturesGeometryServices(Substitute.For<IGeometryService>(),
                    coordinateTransform, limits, NullLogger<OgcFeaturesGeometryServices>.Instance),
                    coordinateTransform, Substitute.For<ICrsRegistry>()));
        }

        public async Task ClaimSecondLayerAsync()
        {
            var claim = await _locks.ClaimAsync(FeatureRef.Canonical("parcels", 2, 22),
                new LockHolder("alice", "Alice Editor"), TimeSpan.FromMinutes(5), FeatureLockAccessContext.AuthorizedWrite);
            claim.Status.Should().Be(FeatureLockClaimStatus.Claimed);
        }

        public async Task<(int Status, string Body)> ExecuteAsync(string action, bool multiLayer, string holder = "bob")
        {
            static string ActionXml(string action, int layer) => $"""
                <wfs:{action} typeName="honua:layer{layer}">
                  {(action == "Update" ? "<wfs:Property><wfs:ValueReference>name</wfs:ValueReference><wfs:Value>changed</wfs:Value></wfs:Property>" : "")}
                  <fes:Filter><fes:ResourceId rid="layer{layer}.{layer * 11}" /></fes:Filter>
                </wfs:{action}>
                """;
            var xml = $"""
                <wfs:Transaction service="WFS" version="2.0.0" rollbackOnFailure="true"
                    xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:fes="http://www.opengis.net/fes/2.0"
                    xmlns:honua="http://honua.io/wfs">
                  {(multiLayer ? ActionXml(action, 1) : "")}
                  {ActionXml(action, 2)}
                </wfs:Transaction>
                """;
            var context = new DefaultHttpContext { RequestServices = _services };
            context.Request.Method = "POST";
            context.Request.Path = "/wfs";
            context.Request.ContentType = "application/xml";
            context.Request.Headers[FeatureEditLockEnforcement.HolderHeaderName] = holder;
            using var requestBody = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            using var responseBody = new MemoryStream();
            context.Request.Body = requestBody;
            context.Response.Body = responseBody;
            _accessor.HttpContext = context;
            var result = await _handler.HandleTransactionAsync(context);
            await result.ExecuteAsync(context);
            return (context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()));
        }

        public void Dispose()
        {
            _accessor.HttpContext = null;
            _services.Dispose();
        }

        private static Feature FeatureWithId(long id) => new()
        {
            Id = id,
            Attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "original")
        };

        private static FeatureEditResult Apply(Dictionary<int, Feature> features, int layer, FeatureEditBatch batch)
        {
            foreach (var operation in batch.Operations)
            {
                if (operation.Kind == FeatureEditOperationKind.Delete) features.Remove(layer);
                else if (operation.Feature is { } feature) features[layer] = feature;
            }

            var updates = batch.Operations.Where(op => op.Kind == FeatureEditOperationKind.Update)
                .Select(op => EditOperationResult.Success(op.Feature!.Value.Id)).ToImmutableArray();
            var deletes = batch.Operations.Where(op => op.Kind == FeatureEditOperationKind.Delete)
                .Select(op => EditOperationResult.Success(op.ObjectId!.Value)).ToImmutableArray();
            return FeatureEditResult.Success(0, updates.Length, deletes.Length, updateResults: updates, deleteResults: deletes);
        }
    }
}
