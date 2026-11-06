// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class ReadPolicyMetadataFailureTests
{
    [UnitTheory]
    [InlineData("row")]
    [InlineData("mask")]
    [InlineData("combined")]
    [InlineData("job-row")]
    [InlineData("job-mask")]
    public async Task ResolveAsync_MetadataServiceScopeFailure_RefusesRead(string path)
    {
        var graph = new ScopeGraphProvider();
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal() } };
        using var job = path.StartsWith("job-", StringComparison.Ordinal)
            ? JobSecurityScope.Begin(new JobSecurityContext("submitter", "tenant-a",
                [new(ClaimTypes.Role, "reader"), new("region", "west")]))
            : null;
        if (job is not null)
        {
            context.HttpContext = null;
        }
        var fixture = new PolicyFixture(graph, context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolveAsync(path, fixture));

        Assert.Equal("Graph unavailable", error.Message);
    }

    [UnitTheory]
    [InlineData("row")]
    [InlineData("mask")]
    public async Task ResolveAsync_MetadataScopeCancellation_PreservesCancellation(string path)
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal() } };
        var fixture = new PolicyFixture(new ScopeGraphProvider(), context);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResolveAsync(path, fixture, cancelled.Token));
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveAsync_ValidServiceScope_PreservesScopedAndWildcardPolicies(bool unpublished)
    {
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal() } };
        var graph = new ScopeGraphProvider();
        var fixture = new PolicyFixture(graph, context, unpublished ? "*" : "alpha");
        graph.Snapshot = CreateSnapshot(fixture.Resource, unpublished ? 0 : 1);

        Assert.NotNull(await fixture.Rows.ResolveAsync(fixture.Resource));
        Assert.Equal("secret", Assert.Single(await fixture.Masks.ResolveAsync(fixture.Resource)));
        var combined = await fixture.Resolver.ApplyAsync(fixture.Resource, new FeatureQuery(), CancellationToken.None);
        Assert.NotNull(combined.EnforcedSqlFilter);
        Assert.Equal("secret", Assert.Single(combined.EnforcedMaskedFields!.Value));
    }

    [UnitTest]
    public async Task ResolveAsync_ValidatedSnapshot_RetainsPoliciesWhenProviderIsUnavailable()
    {
        var context = new DefaultHttpContext { User = Principal() };
        var graph = new ScopeGraphProvider();
        var fixture = new PolicyFixture(graph, new HttpContextAccessor { HttpContext = context });
        ValidatedMetadataSnapshot.Remember(context, fixture.Resource, CreateSnapshot(fixture.Resource, 1));

        Assert.NotNull(await fixture.Rows.ResolveAsync(fixture.Resource));
        Assert.Equal("secret", Assert.Single(await fixture.Masks.ResolveAsync(fixture.Resource)));
        var combined = await fixture.Resolver.ApplyAsync(fixture.Resource, new FeatureQuery(), CancellationToken.None);
        Assert.NotNull(combined.EnforcedSqlFilter);
        Assert.Equal("secret", Assert.Single(combined.EnforcedMaskedFields!.Value));
        Assert.Equal(0, graph.Calls);
    }

    private static MetadataV2GraphSnapshot CreateSnapshot(MetadataV2Resource resource, int serviceCount)
        => new(new MetadataV2Graph
        {
            Resources = [resource],
            Services = serviceCount == 0 ? [] : [new() { Metadata = new() { Id = "service", Name = "alpha" } }],
            Publications = serviceCount == 0 ? [] : [new()
            {
                Metadata = new() { Id = "publication" }, ServiceId = "service", ResourceId = "resource"
            }]
        }, "test", DateTimeOffset.UtcNow);

    private sealed class ScopeGraphProvider : IMetadataV2GraphProvider
    {
        internal MetadataV2GraphSnapshot? Snapshot { get; set; }
        internal int Calls { get; private set; }

        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Snapshot is null
                ? ValueTask.FromException<MetadataV2GraphSnapshot>(new InvalidOperationException("Graph unavailable"))
                : ValueTask.FromResult(Snapshot);
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }

    private static Task ResolveAsync(string path, PolicyFixture fixture, CancellationToken cancellationToken = default)
        => path switch
        {
            "row" or "job-row" => fixture.Rows.ResolveAsync(fixture.Resource, cancellationToken),
            "mask" or "job-mask" => fixture.Masks.ResolveAsync(fixture.Resource, cancellationToken),
            "combined" => fixture.Resolver.ApplyAsync(fixture.Resource, new FeatureQuery(), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };

    private static ClaimsPrincipal Principal()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "reader"), new Claim("region", "west")], "Test"));

    private sealed class PolicyFixture
    {
        internal MetadataV2Resource Resource { get; } = new() { Metadata = new() { Id = "resource", Name = "parcels" } };
        internal RowLevelSecurityFilterSource Rows { get; }
        internal FieldMaskSource Masks { get; }
        internal LayerReadSecurityResolver Resolver { get; }

        internal PolicyFixture(ScopeGraphProvider graph, HttpContextAccessor context, string policyService = "alpha")
        {
            var rows = Substitute.For<IRlsPolicyStore, ICombinedReadPolicyStore>();
            var fields = Substitute.For<IFieldMaskPolicyStore>();
            var combined = (ICombinedReadPolicyStore)rows;
            combined.CanResolveWith(fields).Returns(true);
            var row = new RlsPolicy { Role = "reader", Service = policyService, Layer = "parcels", Attribute = "region", ClaimType = "region" };
            var mask = new FieldMaskPolicy { Role = "reader", Service = policyService, Layer = "parcels", Attribute = "secret" };
            rows.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), "parcels", Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlyList<RlsPolicy>>(Matches(call.ArgAt<string>(1)) ? [row] : []));
            fields.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), "parcels", Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlyList<FieldMaskPolicy>>(Matches(call.ArgAt<string>(1)) ? [mask] : []));
            combined.GetEffectiveReadPoliciesAsync(fields, Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<IReadOnlyCollection<string>>(), "parcels", Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(call.ArgAt<IReadOnlyCollection<string>>(2).Any(Matches) || policyService == "*"
                    ? new EffectiveReadPolicies([row], [mask])
                    : new EffectiveReadPolicies([], [])));
            var filters = Substitute.For<IFilterExpressionService>();
            filters.Normalize(Arg.Any<FilterExpression>(), Resource).Returns(call => call.ArgAt<FilterExpression>(0));
            filters.Translate(Arg.Any<FilterExpression>(), Resource).Returns(call =>
                FilterTranslationResult.Success(call.ArgAt<FilterExpression>(0), new SqlFragment("region = @p0", ["west"])));
            var options = Options.Create(new RbacOptions());
            Rows = new RowLevelSecurityFilterSource(context, rows, graph, filters, options, NullLogger<RowLevelSecurityFilterSource>.Instance);
            Masks = new FieldMaskSource(context, fields, graph, options, NullLogger<FieldMaskSource>.Instance);
            Resolver = new LayerReadSecurityResolver(graph, filters, Rows, Masks);

            bool Matches(string service) => policyService == "*" || service == policyService;
        }
    }
}
