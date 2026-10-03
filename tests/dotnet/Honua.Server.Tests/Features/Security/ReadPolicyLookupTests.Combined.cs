// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Security;

public sealed partial class ReadPolicyLookupTests
{
    [UnitTest]
    public async Task ApplyAsync_CombinedPolicies_ReadsCurrentPrincipalPoliciesAndAllScopesOnEveryOperation()
    {
        var resource = new MetadataV2Resource { Metadata = new() { Id = "resource", Name = "parcels" } };
        var graph = CreateGraph(resource, 2);
        var context = new DefaultHttpContext { User = Principal("reader", "west") };
        var accessor = new HttpContextAccessor { HttpContext = context };
        ValidatedMetadataSnapshot.Remember(context, resource, await graph.GetCurrentAsync());
        var rows = Substitute.For<IRlsPolicyStore, ICombinedReadPolicyStore>();
        var joint = (ICombinedReadPolicyStore)rows;
        var fields = Substitute.For<IFieldMaskPolicyStore>();
        joint.CanResolveWith(fields).Returns(true);
        var calls = new List<string[]>();
        var scopeCalls = new List<string[]>();
        var policyFields = new List<FieldMaskPolicy> { Mask(" secret "), Mask("SECRET") };
        joint.GetEffectiveReadPoliciesAsync(fields, Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyCollection<string>>(), "parcels", Arg.Any<CancellationToken>()).Returns(call =>
            {
                calls.Add(call.ArgAt<IReadOnlyList<string>>(1).ToArray());
                scopeCalls.Add(call.ArgAt<IReadOnlyCollection<string>>(2).ToArray());
                return Task.FromResult(new EffectiveReadPolicies(
                    [new RlsPolicy { Role = "*", Service = "*", Layer = "*", Attribute = "region", ClaimType = "region" }],
                    policyFields.ToArray()));
            });
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Normalize(Arg.Any<FilterExpression>(), resource).Returns(call => call.ArgAt<FilterExpression>(0));
        filters.Translate(Arg.Any<FilterExpression>(), resource).Returns(call =>
        {
            var expression = call.ArgAt<FilterExpression>(0);
            var values = ((ValueList)((BinaryExpression)expression).Right).Values.Cast<Literal>().Select(v => v.Value).ToArray();
            return FilterTranslationResult.Success(expression, new SqlFragment(values.Length == 0 ? "FALSE" : "region = @p0", values));
        });
        var options = Options.Create(new RbacOptions());
        var rls = new RowLevelSecurityFilterSource(accessor, rows, graph, filters, options, NullLogger<RowLevelSecurityFilterSource>.Instance);
        var masks = new FieldMaskSource(accessor, fields, graph, options, NullLogger<FieldMaskSource>.Instance);
        var resolver = new LayerReadSecurityResolver(graph, filters, rls, masks);

        var first = await resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);
        first.EnforcedSqlFilter!.Parameters.Should().Equal("west");
        first.EnforcedMaskedFields!.Value.Should().Equal("secret");
        policyFields.Add(Mask("new_secret"));
        context.User = Principal("other", "east");
        var second = await resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);
        second.EnforcedSqlFilter!.Parameters.Should().Equal("east");
        second.EnforcedMaskedFields!.Value.Should().BeEquivalentTo("secret", "new_secret");
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        var anonymous = await resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);
        anonymous.EnforcedSqlFilter!.Sql.Should().Be("FALSE");
        anonymous.EnforcedMaskedFields!.Value.Should().Contain("secret");
        calls.Should().HaveCount(3);
        calls[0].Should().Contain("reader");
        calls[1].Should().Contain("other");
        calls[2].Should().BeEmpty();
        scopeCalls.Should().OnlyContain(scopes => scopes.Length == 2 && scopes.Contains("alpha") && scopes.Contains("beta"));
        graph.Calls.Should().Be(1);
        await rows.DidNotReceive().GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fields.DidNotReceive().GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void CanResolveWith_IndependentContextOptionsStoreOrCustomSource_RejectsCombination()
    {
        var resource = new MetadataV2Resource { Metadata = new() { Id = "resource", Name = "parcels" } };
        var graph = CreateGraph(resource, 1);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var options = Options.Create(new RbacOptions());
        var rows = Substitute.For<IRlsPolicyStore, ICombinedReadPolicyStore>();
        var fields = Substitute.For<IFieldMaskPolicyStore>();
        var jointStore = (ICombinedReadPolicyStore)rows;
        jointStore.CanResolveWith(fields).Returns(true);
        var source = new RowLevelSecurityFilterSource(accessor, rows, graph, Substitute.For<IFilterExpressionService>(), options, NullLogger<RowLevelSecurityFilterSource>.Instance);
        var combined = Assert.IsAssignableFrom<ICombinedReadSecuritySource>(source);
        combined.CanResolveWith(Masks(accessor, options, fields)).Should().BeTrue();
        combined.CanResolveWith(Masks(new HttpContextAccessor(), options, fields)).Should().BeFalse();
        combined.CanResolveWith(Masks(accessor, Options.Create(new RbacOptions()), fields)).Should().BeFalse();
        combined.CanResolveWith(Masks(accessor, options, Substitute.For<IFieldMaskPolicyStore>())).Should().BeFalse();
        combined.CanResolveWith(Substitute.For<IFieldMaskSource>()).Should().BeFalse();
        accessor.HttpContext = null;
        combined.CanResolveWith(Masks(accessor, options, fields)).Should().BeFalse();

        FieldMaskSource Masks(IHttpContextAccessor context, IOptions<RbacOptions> opts, IFieldMaskPolicyStore store)
            => new(context, store, graph, opts, NullLogger<FieldMaskSource>.Instance);
    }

    [UnitTest]
    public async Task ResolveCombinedAsync_CancellationOrStoreFailure_RefusesRead()
    {
        var resource = new MetadataV2Resource { Metadata = new() { Id = "resource", Name = "parcels" } };
        var graph = CreateGraph(resource, 1);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var rows = Substitute.For<IRlsPolicyStore, ICombinedReadPolicyStore>();
        var fields = Substitute.For<IFieldMaskPolicyStore>();
        var store = (ICombinedReadPolicyStore)rows;
        store.CanResolveWith(fields).Returns(true);
        var options = Options.Create(new RbacOptions());
        var rls = new RowLevelSecurityFilterSource(accessor, rows, graph, Substitute.For<IFilterExpressionService>(), options, NullLogger<RowLevelSecurityFilterSource>.Instance);
        var masks = new FieldMaskSource(accessor, fields, graph, options, NullLogger<FieldMaskSource>.Instance);
        var resolver = new LayerReadSecurityResolver(graph, null, rls, masks);
        store.GetEffectiveReadPoliciesAsync(fields, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<string>>(), "parcels", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EffectiveReadPolicies>(new InvalidOperationException("catalog unavailable")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        store.GetEffectiveReadPoliciesAsync(fields, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyCollection<string>>(), "parcels", cancelled.Token)
            .Returns(Task.FromCanceled<EffectiveReadPolicies>(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ApplyAsync(resource, new FeatureQuery(), cancelled.Token));
    }

    private static ClaimsPrincipal Principal(string role, string region)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role), new Claim("region", region)], "Test"));

    private static FieldMaskPolicy Mask(string attribute)
        => new() { Role = "*", Service = "*", Layer = "*", Attribute = attribute };
}
