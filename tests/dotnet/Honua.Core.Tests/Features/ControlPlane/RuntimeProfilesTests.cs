// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.ControlPlane;

public sealed class RuntimeProfilesTests
{
    public static IEnumerable<object[]> ExclusiveClaimMatrix()
    {
        var workers = new[]
        {
            (RuntimeProfiles.Managed, RuntimeProfiles.DefaultAccepted),
            (RuntimeProfiles.Native, RuntimeProfiles.NativeAccepted),
            (RuntimeProfiles.RasterPostgis, RuntimeProfiles.RasterPostgisAccepted),
            (RuntimeProfiles.CustomCode, RuntimeProfiles.CustomCodeAccepted),
        };

        foreach (var worker in workers)
        {
            foreach (var jobProfile in workers.Select(candidate => candidate.Item1))
            {
                yield return [worker.Item1, worker.Item2, jobProfile, worker.Item1 == jobProfile];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ExclusiveClaimMatrix))]
    public void CanClaim_ExclusiveWorkerProfile_ClaimsOnlyMatchingJobs(
        string workerProfile,
        IReadOnlySet<string> acceptedProfiles,
        string jobProfile,
        bool expected)
    {
        acceptedProfiles.Should().Equal(workerProfile);
        RuntimeProfiles.CanClaim(acceptedProfiles, jobProfile).Should().Be(expected);
    }

    [UnitTest]
    public void CanClaim_UnspecifiedWorkerAndLegacyJob_NormalizeToManagedOnly()
    {
        RuntimeProfiles.CanClaim(null, null).Should().BeTrue();
        RuntimeProfiles.CanClaim(null, string.Empty).Should().BeTrue();
        RuntimeProfiles.CanClaim(null, RuntimeProfiles.RasterPostgis).Should().BeFalse();
    }
}
