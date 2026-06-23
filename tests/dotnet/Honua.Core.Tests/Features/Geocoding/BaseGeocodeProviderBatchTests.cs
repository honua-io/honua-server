// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Core.Tests.Features.Geocoding;

public sealed class BaseGeocodeProviderBatchTests
{
    [Fact]
    public async Task BatchGeocodeAsync_ReturnsOneSlotPerInput_PreservingPositionalAlignment()
    {
        var provider = new RecordingBatchProvider();

        var results = await provider.BatchGeocodeAsync(
            new BatchGeocodeRequest(["10 Downing St", "   ", "350 Fifth Ave"], 4326),
            CancellationToken.None);

        Assert.Equal(3, results.Count);

        // Index 0 matched; index 1 is blank (no match); index 2 matched.
        Assert.True(results[0].IsMatch);
        Assert.Equal("0", results[0].Attributes[GeocodeCandidate.ResultIdAttribute]);

        Assert.False(results[1].IsMatch);
        Assert.Equal("1", results[1].Attributes[GeocodeCandidate.ResultIdAttribute]);

        Assert.True(results[2].IsMatch);
        Assert.Equal("2", results[2].Attributes[GeocodeCandidate.ResultIdAttribute]);

        // Blank inputs do not call the forward geocoder.
        Assert.Equal(2, provider.ForwardCalls.Count);
    }

    [Fact]
    public async Task BatchGeocodeAsync_FanOutHonorsMaxResultsPerQuery()
    {
        var provider = new RecordingBatchProvider();

        // MaxResultsPerQuery must flow through to each forward request so it is no longer a silent
        // no-op (issue #2005).
        await provider.BatchGeocodeAsync(
            new BatchGeocodeRequest(["10 Downing St", "350 Fifth Ave"], 4326)
            {
                MaxResultsPerQuery = 5
            },
            CancellationToken.None);

        Assert.Equal(2, provider.ForwardCalls.Count);
        Assert.All(provider.ForwardCalls, forward => Assert.Equal(5, forward.MaxResults));
    }

    [Fact]
    public async Task BatchGeocodeAsync_EmitsEveryCandidatePerInput_SharingResultId()
    {
        // Each input returns two candidates; the batch path must emit both, in order, each stamped
        // with the originating input index so a multi-candidate input stays correlated (issue #2005).
        var provider = new RecordingBatchProvider { CandidatesPerInput = 2 };

        var results = await provider.BatchGeocodeAsync(
            new BatchGeocodeRequest(["10 Downing St", "350 Fifth Ave"], 4326)
            {
                MaxResultsPerQuery = 2
            },
            CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal("0", results[0].Attributes[GeocodeCandidate.ResultIdAttribute]);
        Assert.Equal("0", results[1].Attributes[GeocodeCandidate.ResultIdAttribute]);
        Assert.Equal("1", results[2].Attributes[GeocodeCandidate.ResultIdAttribute]);
        Assert.Equal("1", results[3].Attributes[GeocodeCandidate.ResultIdAttribute]);
        Assert.All(results, candidate => Assert.True(candidate.IsMatch));
    }

    private sealed class RecordingBatchProvider : BaseGeocodeProvider
    {
        public List<ForwardGeocodeRequest> ForwardCalls { get; } = [];

        public int CandidatesPerInput { get; init; } = 1;

        public override string Name => "recording";

        public override GeocodeProviderCapabilities Capabilities => new(
            SupportsForwardGeocode: true,
            SupportsBatch: true);

        public override Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
            ForwardGeocodeRequest request,
            CancellationToken cancellationToken = default)
        {
            ForwardCalls.Add(request);

            var candidates = new List<GeocodeCandidate>(CandidatesPerInput);
            for (var i = 0; i < CandidatesPerInput; i++)
            {
                candidates.Add(new GeocodeCandidate(request.Query, 1.0, 2.0, 90.0 - i, new Dictionary<string, string?>()));
            }

            return Task.FromResult<IReadOnlyList<GeocodeCandidate>>(candidates);
        }

        public override Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
            ReverseGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ReverseGeocodeMatch?>(null);
    }
}
