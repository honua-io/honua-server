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
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActiveJob_UsesSubmitterAndCurrentGraphInsteadOfAmbientRequest(bool hasSubmitter)
    {
        var resource = new MetadataV2Resource { Metadata = new() { Id = "resource", Name = "parcels" } };
        var graph = CreateGraph(resource, 1);
        var context = new DefaultHttpContext { User = Principal("admin", "east") };
        var accessor = new HttpContextAccessor { HttpContext = context };
        // A snapshot remembered by an unrelated HTTP request cannot suppress a job's
        // current service-scoped policies, even if its resource reference is identical.
        ValidatedMetadataSnapshot.Remember(context, resource, (await CreateGraph(resource, 0).GetCurrentAsync()));
        var rows = new InMemoryRlsPolicyStore();
        var fields = new InMemoryFieldMaskPolicyStore();
        await rows.CreatePolicyAsync(new RlsPolicy
        {
            Role = "reader", Service = "alpha", Layer = "parcels", Attribute = "region", ClaimType = "region"
        });
        await fields.CreatePolicyAsync(new FieldMaskPolicy
        {
            Role = "reader", Service = "alpha", Layer = "parcels", Attribute = "secret"
        });
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Normalize(Arg.Any<FilterExpression>(), resource).Returns(call => call.ArgAt<FilterExpression>(0));
        filters.Translate(Arg.Any<FilterExpression>(), resource).Returns(call =>
        {
            var expression = call.ArgAt<FilterExpression>(0);
            var values = ((ValueList)((BinaryExpression)expression).Right).Values.Cast<Literal>().Select(value => value.Value).ToArray();
            return FilterTranslationResult.Success(expression, new SqlFragment("region = @p0", values));
        });
        var options = Options.Create(new RbacOptions());
        var rls = new RowLevelSecurityFilterSource(accessor, rows, graph, filters, options, NullLogger<RowLevelSecurityFilterSource>.Instance);
        var masks = new FieldMaskSource(accessor, fields, graph, options, NullLogger<FieldMaskSource>.Instance);
        using (JobSecurityScope.Begin(hasSubmitter ? new JobSecurityContext("submitter", null,
            [new(ClaimTypes.Role, "reader"), new("region", "west")]) : null))
        {
            if (hasSubmitter)
            {
                (await rls.ResolveAsync(resource))!.Parameters.Should().Equal("west");
                (await masks.ResolveAsync(resource)).Should().Equal("secret");
            }
            else
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rls.ResolveAsync(resource));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => masks.ResolveAsync(resource));
            }
        }
        (await rls.ResolveAsync(resource)).Should().BeNull();
        (await masks.ResolveAsync(resource)).Should().BeEmpty();
    }

    [UnitTest]
    public async Task CombinedPolicies_UseJobSubmitterWithAmbientAdministrator()
    {
        var resource = new MetadataV2Resource { Metadata = new() { Id = "resource", Name = "parcels" } };
        var graph = CreateGraph(resource, 1);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal("admin", "east") } };
        var rows = Substitute.For<IRlsPolicyStore, ICombinedReadPolicyStore>();
        var fields = Substitute.For<IFieldMaskPolicyStore>();
        var joint = (ICombinedReadPolicyStore)rows;
        joint.CanResolveWith(fields).Returns(true);
        joint.GetEffectiveReadPoliciesAsync(fields, Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyCollection<string>>(), "parcels", Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<IReadOnlyList<string>>(1).Contains("reader")
                ? new EffectiveReadPolicies(
                    [new RlsPolicy { Role = "reader", Service = "*", Layer = "parcels", Attribute = "region", ClaimType = "region" }],
                    [new FieldMaskPolicy { Role = "reader", Service = "*", Layer = "parcels", Attribute = "secret" }])
                : new EffectiveReadPolicies([], [])));
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Normalize(Arg.Any<FilterExpression>(), resource).Returns(call => call.ArgAt<FilterExpression>(0));
        filters.Translate(Arg.Any<FilterExpression>(), resource).Returns(call =>
        {
            var expression = call.ArgAt<FilterExpression>(0);
            var values = ((ValueList)((BinaryExpression)expression).Right).Values.Cast<Literal>().Select(value => value.Value).ToArray();
            return FilterTranslationResult.Success(expression, new SqlFragment("region = @p0", values));
        });
        var options = Options.Create(new RbacOptions());
        var rls = new RowLevelSecurityFilterSource(accessor, rows, graph, filters, options, NullLogger<RowLevelSecurityFilterSource>.Instance);
        var masks = new FieldMaskSource(accessor, fields, graph, options, NullLogger<FieldMaskSource>.Instance);
        var resolver = new LayerReadSecurityResolver(graph, filters, rls, masks);
        using var scope = JobSecurityScope.Begin(new JobSecurityContext("submitter", null,
            [new(ClaimTypes.Role, "reader"), new("region", "west")]));

        var query = await resolver.ApplyAsync(resource, new FeatureQuery(), CancellationToken.None);

        query.EnforcedSqlFilter!.Parameters.Should().Equal("west");
        query.EnforcedMaskedFields!.Value.Should().Equal("secret");
    }
}
