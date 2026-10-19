// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Xunit;

namespace Honua.Core.Tests.Features.Authorization;

public sealed class JobResourcePolicyFingerprintTests
{
    [Fact]
    public void Fingerprint_NormalizesMaskOrderAndCaseWithoutChangingPolicyParameters()
    {
        var resource = Resource();
        var policy = new ReadSecurityResolution(new SqlFragment("department = @p0", ["planning"]), ["secret", "internal"]);
        var reordered = policy with { MaskedFields = ["INTERNAL", "SECRET"] };
        Assert.Equal(JobResourcePolicyFingerprint.Compute(resource, policy), JobResourcePolicyFingerprint.Compute(resource, reordered));
        Assert.NotEqual(JobResourcePolicyFingerprint.Compute(resource, policy), JobResourcePolicyFingerprint.Compute(resource,
            policy with { RowFilter = new("department = @p0", ["admin"]) }));
        Assert.NotEqual(JobResourcePolicyFingerprint.Compute(resource, policy), JobResourcePolicyFingerprint.Compute(resource,
            policy with { MaskedFields = ["secret"] }));
        Assert.NotEqual(JobResourcePolicyFingerprint.Compute(resource, policy), JobResourcePolicyFingerprint.Compute(
            new() { Metadata = new() { Id = "other", Name = "parcels" } }, policy));
    }

    [Fact]
    public void Fingerprint_DistinguishesNullAndScalarTypesAndParameterOrder()
    {
        var resource = Resource();
        string Hash(params object?[] values) => JobResourcePolicyFingerprint.Compute(resource,
            new(new("value = @p0", values), ImmutableArray<string>.Empty));
        Assert.NotEqual(Hash((object?)null), Hash("null"));
        Assert.NotEqual(Hash(1), Hash("1"));
        Assert.NotEqual(Hash(1), Hash(1L));
        Assert.NotEqual(Hash("a", "b"), Hash("b", "a"));
    }

    [Fact]
    public void Fingerprint_UsesInvariantNumericAndTemporalEncodings()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var policy = new ReadSecurityResolution(new("value = @p0", [1.5m, 3.125d,
                DateTimeOffset.Parse("2026-10-18T10:00:00+02:00", CultureInfo.InvariantCulture)]), ImmutableArray<string>.Empty);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var expected = JobResourcePolicyFingerprint.Compute(Resource(), policy);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, JobResourcePolicyFingerprint.Compute(Resource(), policy));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Fingerprint_RejectsUnboundedOrUnsupportedPolicyValues()
    {
        Assert.Throws<UnauthorizedAccessException>(() => JobResourcePolicyFingerprint.Compute(Resource(), new(null, default)));
        Assert.Throws<UnauthorizedAccessException>(() => JobResourcePolicyFingerprint.Compute(Resource(),
            new(new("value = @p0", [new object()]), ImmutableArray<string>.Empty)));
        Assert.Throws<UnauthorizedAccessException>(() => JobResourcePolicyFingerprint.Compute(Resource(),
            new(new("value = @p0", [new string('x', 65537)]), ImmutableArray<string>.Empty)));
        Assert.Throws<UnauthorizedAccessException>(() => JobResourcePolicyFingerprint.Compute(Resource(),
            new(new(new string('x', 65537), []), ImmutableArray<string>.Empty)));
    }

    private static MetadataV2Resource Resource() => new() { Metadata = new() { Id = "resource", Name = "parcels" } };
}
