// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Services;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// A gRPC <c>where</c> clause that consumes a field masked from the caller is refused before
/// any count, object id or feature is read, for every operator family of the filter grammar
/// and on every query shape (count-only, ids-only, features, streamed pages). Unmasked
/// predicates are bound to the shared filter parser and translator (SEC-11).
/// </summary>
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcMaskedFieldPredicateTests
{
    private const string MaskedField = "secret";

    private readonly IResourceValidator _resourceValidator = Substitute.For<IResourceValidator>();
    private readonly IFeatureReader _featureReader = Substitute.For<IFeatureReader>();
    private readonly IStreamingFeatureStore _streamingStore = Substitute.For<IStreamingFeatureStore>();
    private readonly IFieldMaskSource _fieldMaskSource = Substitute.For<IFieldMaskSource>();
    private readonly ISqlFilterTranslator _sqlFilterTranslator = Substitute.For<ISqlFilterTranslator>();
    private readonly HonuaFeatureService _sut;

    private static readonly MetadataV2Resource _resource = CreateResource();
    private static readonly MetadataV2Service _service = CreateService();

    public GrpcMaskedFieldPredicateTests()
    {
        var crsRegistry = Substitute.For<ICrsRegistry>();
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        crsRegistry
            .IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<bool>(true));
#pragma warning restore CA2012

        _sut = new HonuaFeatureService(
            _resourceValidator,
            _featureReader,
            Substitute.For<IFeatureWriter>(),
            _streamingStore,
            new CommonQueryValidator(Options.Create(new LimitsOptions())),
            new SpatialReferenceResolver(Substitute.For<ICrsDetectionService>(), crsRegistry),
            new FeatureMutationEventService(
                Substitute.For<IFeatureChangeEventPublisher>(),
                outboxCapabilityProvider: Substitute.For<IOutboxCapabilityProvider>()),
            Options.Create(new LimitsOptions()),
            Options.Create(new GrpcOptions()),
            NullLogger<HonuaFeatureService>.Instance,
            new GrpcApplyEditsIdempotencyStore());

        _resourceValidator
            .ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(CreateTriple()));

        _fieldMaskSource
            .ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(MaskedField));

        _sqlFilterTranslator
            .Translate(Arg.Any<FilterExpression>(), Arg.Any<MetadataV2Resource>())
            .Returns(new SqlFragment("TRUE", []));

        _featureReader
            .CountAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(1L);
        _featureReader
            .QueryObjectIdsAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(1L));
        _featureReader
            .QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, ImmutableArray.Create(Feature.Create(1, null))));
        _streamingStore
            .StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => SingleFeature());
    }

    public enum QueryShape
    {
        CountOnly,
        CountOnlyForOneObjectId,
        IdsOnly,
        Features,
        Stream
    }

    public static TheoryData<string, QueryShape> MaskedPredicateCases()
    {
        string[] predicates =
        [
            "secret = 'alpha'",
            "secret LIKE 'a%'",
            "secret NOT LIKE 'a%'",
            "secret IN ('alpha', 'beta')",
            "secret NOT IN ('alpha', 'beta')",
            "secret IS NULL",
            "secret IS NOT NULL",
            "secret BETWEEN 'a' AND 'b'",
            "secret NOT BETWEEN 'a' AND 'b'",
            "secret >= 'a'",
            "secret < 'b'",
            "secret <> 'alpha'",
        ];

        var cases = new TheoryData<string, QueryShape>();
        foreach (var predicate in predicates)
        {
            foreach (var shape in Enum.GetValues<QueryShape>())
            {
                cases.Add(predicate, shape);
            }
        }

        return cases;
    }

    [UnitTheory]
    [MemberData(nameof(MaskedPredicateCases))]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WhereConsumesMaskedField_IsRefusedBeforeAnyRead(string where, QueryShape shape)
    {
        var act = () => ExecuteAsync(where, shape);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        exception.Which.Status.Detail.Should().Contain(MaskedField).And.Contain("masked");

        AssertNoRead();
    }

    [UnitTheory]
    [InlineData("name NOT LIKE 'a%'", QueryShape.CountOnly)]
    [InlineData("name NOT IN ('secret')", QueryShape.IdsOnly)]
    [InlineData("name = 'secret' OR name IS NULL", QueryShape.Features)]
    [InlineData("name BETWEEN 'a' AND 'b'", QueryShape.Stream)]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WhereOnReadableField_IsTranslatedBySharedFilterPipeline(string where, QueryShape shape)
    {
        var captured = new List<FeatureQuery>();
        _featureReader
            .CountAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(1L);
        _featureReader
            .QueryObjectIdsAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(1L));
        _featureReader
            .QueryAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, ImmutableArray.Create(Feature.Create(1, null))));
        _streamingStore
            .StreamFeaturesAsync(Arg.Any<int>(), Arg.Do<FeatureQuery>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(_ => SingleFeature());

        await ExecuteAsync(where, shape);

        var query = captured.Should().ContainSingle().Subject;
        query.SqlFilter.Should().NotBeNull();
        query.Where.Should().Be(where);
        _sqlFilterTranslator.Received(1).Translate(Arg.Any<FilterExpression>(), _resource);
    }

    [UnitTest]
    [Endpoint("POST /grpc/geospatial.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WhereOutsideSharedGrammar_IsRefusedBeforeAnyRead()
    {
        // Provider-specific accessor syntax is not part of the shared filter grammar, so it
        // no longer reaches a provider's own WHERE parser.
        var act = () => ExecuteAsync("attributes->>'name' NOT LIKE 'a%'", QueryShape.CountOnly);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);

        AssertNoRead();
    }

    private async Task ExecuteAsync(string where, QueryShape shape)
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = where,
            ReturnCountOnly = shape is QueryShape.CountOnly or QueryShape.CountOnlyForOneObjectId,
            ReturnIdsOnly = shape is QueryShape.IdsOnly
        };
        if (shape is QueryShape.CountOnlyForOneObjectId)
        {
            request.ObjectIds.Add(1L);
        }

        using var context = CreateCallContext();
        if (shape is QueryShape.Stream)
        {
            await _sut.QueryFeaturesStream(request, new DiscardingStreamWriter(), context);
            return;
        }

        await _sut.QueryFeatures(request, context);
    }

    private void AssertNoRead()
    {
        _ = _featureReader.DidNotReceiveWithAnyArgs().CountAsync(default, default!, default);
        _ = _featureReader.DidNotReceiveWithAnyArgs().QueryObjectIdsAsync(default, default!, default);
        _ = _featureReader.DidNotReceiveWithAnyArgs().QueryAsync(default, default!, default);
        _ = _streamingStore.DidNotReceiveWithAnyArgs().StreamFeaturesAsync(default, default!, default);
    }

    private static async IAsyncEnumerable<Feature> SingleFeature()
    {
        await Task.Yield();
        yield return Feature.Create(1, null);
    }

    private TestServerCallContext CreateCallContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton(Substitute.For<ITenantContext>());
        services.AddSingleton(_fieldMaskSource);
        services.AddSingleton<IFilterExpressionService>(
            new FilterExpressionService(new FilterExpressionTranslator(_sqlFilterTranslator)));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "reader"), new Claim(ClaimTypes.Role, "admin")],
                "Test"))
        };

        var context = new TestServerCallContext();
        context.UserState["__HttpContext"] = httpContext;
        return context;
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "resource-masked",
                Name = "masked"
            },
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field
                {
                    Name = "name",
                    Type = MetadataV2FieldType.String,
                    Length = 255,
                    Nullable = true
                },
                new MetadataV2Field
                {
                    Name = MaskedField,
                    Type = MetadataV2FieldType.String,
                    Length = 255,
                    Nullable = true
                }
            ]
        };

    private static MetadataV2Service CreateService()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "service-test",
                Name = "test"
            },
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            Protocols = ["Grpc"]
        };

    private static MetadataV2ServiceLayerTriple CreateTriple()
        => new(
            _service,
            new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = "publication-test-masked",
                    Name = "masked"
                },
                ServiceId = _service.Metadata.Id,
                ResourceId = _resource.Metadata.Id,
                Identifier = new MetadataV2PublicationIdentifier
                {
                    Value = "0",
                    IsNumeric = true
                },
                IsPrimary = true
            },
            _resource)
        {
            StorageLayerId = 0
        };

    private sealed class DiscardingStreamWriter : IServerStreamWriter<Proto.FeaturePage>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Proto.FeaturePage message) => Task.CompletedTask;

        public Task WriteAsync(Proto.FeaturePage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestServerCallContext : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public void Dispose() => _cts.Dispose();
        protected override string MethodCore => "/geospatial.v1.FeatureService/QueryFeatures";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cts.Token;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
