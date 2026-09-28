// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
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

public sealed class ReadPolicyLookupTests
{
    [UnitTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task ResolveAsync_PublicationScopes_PreservesPoliciesWithoutRedundantLookups(int serviceCount)
    {
        var rlsStore = new InMemoryRlsPolicyStore();
        var maskStore = new InMemoryFieldMaskPolicyStore();
        foreach (var service in new[] { "*", "ALPHA", "beta", "other" })
        {
            await AddPoliciesAsync(service, "reader", "parcels", service == "*" ? "global" : service.ToLowerInvariant());
        }

        await AddPoliciesAsync("*", "*", "*", "all_callers");
        await AddPoliciesAsync("*", "different-role", "parcels", "wrong_role");
        await AddPoliciesAsync("*", "reader", "different-layer", "wrong_layer");

        var rlsCalls = new List<string>();
        var maskCalls = new List<string>();
        var recordingRls = Substitute.For<IRlsPolicyStore>();
        recordingRls.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                rlsCalls.Add(call.ArgAt<string>(1));
                return rlsStore.GetEffectivePoliciesAsync(call.ArgAt<IReadOnlyList<string>>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<CancellationToken>(3));
            });
        var recordingMasks = Substitute.For<IFieldMaskPolicyStore>();
        recordingMasks.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                maskCalls.Add(call.ArgAt<string>(1));
                return maskStore.GetEffectivePoliciesAsync(call.ArgAt<IReadOnlyList<string>>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<CancellationToken>(3));
            });

        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "parcels" }
        };
        var graph = CreateGraph(resource, serviceCount);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "reader"), new Claim("region", "west")], "Test"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Normalize(Arg.Any<FilterExpression>(), resource).Returns(call => call.ArgAt<FilterExpression>(0));
        filters.Translate(Arg.Any<FilterExpression>(), resource)
            .Returns(call => FilterTranslationResult.Success(call.ArgAt<FilterExpression>(0), new SqlFragment("TRUE", [])));
        var options = Options.Create(new RbacOptions());
        var rls = new RowLevelSecurityFilterSource(accessor, recordingRls, graph, filters, options, NullLogger<RowLevelSecurityFilterSource>.Instance);
        var masks = new FieldMaskSource(accessor, recordingMasks, graph, options, NullLogger<FieldMaskSource>.Instance);

        var expressions = await rls.ResolveExpressionsAsync(resource);
        var maskedFields = await masks.ResolveAsync(resource);

        var expectedFields = new List<string> { "global", "all_callers" };
        if (serviceCount >= 1)
        {
            expectedFields.Add("alpha");
        }
        if (serviceCount >= 2)
        {
            expectedFields.Add("beta");
        }

        expressions.Select(expression => ((PropertyReference)((BinaryExpression)expression).Left).PropertyName)
            .Should().BeEquivalentTo(expectedFields, "wildcard policies remain present exactly once across all publications");
        maskedFields.Should().BeEquivalentTo(expectedFields);
        var expectedServices = serviceCount <= 0 ? new[] { "*" } : new[] { "alpha", "beta" }.Take(serviceCount).ToArray();
        rlsCalls.Should().BeEquivalentTo(expectedServices);
        maskCalls.Should().BeEquivalentTo(expectedServices);

        // A new policy must apply on the next resolution even on these same scoped sources.
        await AddPoliciesAsync("*", "reader", "parcels", "new_restriction");
        (await rls.ResolveExpressionsAsync(resource)).Should().HaveCount(expectedFields.Count + 1);
        (await masks.ResolveAsync(resource)).Should().Contain("new_restriction");

        async Task AddPoliciesAsync(string service, string role, string layer, string attribute)
        {
            await rlsStore.CreatePolicyAsync(new RlsPolicy
            {
                Service = service,
                Role = role,
                Layer = layer,
                Attribute = attribute,
                ClaimType = "region"
            });
            await maskStore.CreatePolicyAsync(new FieldMaskPolicy
            {
                Service = service,
                Role = role,
                Layer = layer,
                Attribute = attribute
            });
        }
    }

    [UnitTest]
    public async Task ResolveAsync_PolicyStoreCancellation_PropagatesToBothSources()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var token = cancellation.Token;
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "parcels" }
        };
        var graph = CreateGraph(resource, 1);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var rlsStore = Substitute.For<IRlsPolicyStore>();
        rlsStore.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), "parcels", token)
            .Returns(Task.FromCanceled<IReadOnlyList<RlsPolicy>>(token));
        var maskStore = Substitute.For<IFieldMaskPolicyStore>();
        maskStore.GetEffectivePoliciesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), "parcels", token)
            .Returns(Task.FromCanceled<IReadOnlyList<FieldMaskPolicy>>(token));
        var options = Options.Create(new RbacOptions());
        var rls = new RowLevelSecurityFilterSource(accessor, rlsStore, graph, Substitute.For<IFilterExpressionService>(), options, NullLogger<RowLevelSecurityFilterSource>.Instance);
        var masks = new FieldMaskSource(accessor, maskStore, graph, options, NullLogger<FieldMaskSource>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rls.ResolveAsync(resource, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => masks.ResolveAsync(resource, token));
    }

    private static StubGraphProvider CreateGraph(MetadataV2Resource resource, int serviceCount)
    {
        if (serviceCount < 0)
        {
            return new StubGraphProvider(null);
        }

        var services = new[] { "alpha", "beta" }.Take(serviceCount)
            .Select(name => new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = name, Name = name } })
            .ToArray();
        var publications = services.SelectMany(service => new[] { "first", "duplicate" }.Select(suffix => new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = service.Metadata.Id + suffix },
            ResourceId = resource.Metadata.Id,
            ServiceId = service.Metadata.Id
        })).ToArray();
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Resources = [resource],
            Services = services,
            Publications = publications
        }, "test", DateTimeOffset.UtcNow);
        return new StubGraphProvider(snapshot);
    }

    private sealed class StubGraphProvider(MetadataV2GraphSnapshot? snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => snapshot is null
                ? ValueTask.FromException<MetadataV2GraphSnapshot>(new InvalidOperationException("Graph unavailable"))
                : ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }
}
