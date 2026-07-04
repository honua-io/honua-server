// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Core.Tests.Features.Geocoding;

/// <summary>
/// Covers <see cref="BaseGeocodeProvider.CheckHealthAsync"/>'s exception path
/// (PA-067/PA-201): the previous bare <c>catch (Exception)</c> swallowed the
/// failure entirely, returning a fixed "Provider health check failed." string
/// with no way to tell what actually broke. The fix surfaces the exception's
/// type and message in <see cref="GeocodeProviderHealth.ErrorMessage"/>.
/// </summary>
public sealed class BaseGeocodeProviderHealthTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenCoreThrows_ReturnsUnhealthyWithExceptionDetail()
    {
        var provider = new ThrowingHealthProvider(new InvalidOperationException("upstream timed out"));

        var health = await provider.CheckHealthAsync(CancellationToken.None);

        Assert.False(health.IsHealthy);
        Assert.NotNull(health.ErrorMessage);
        Assert.Contains(nameof(InvalidOperationException), health.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("upstream timed out", health.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenCoreSucceeds_ReturnsHealthyWithNoErrorMessage()
    {
        var provider = new ThrowingHealthProvider(exception: null);

        var health = await provider.CheckHealthAsync(CancellationToken.None);

        Assert.True(health.IsHealthy);
        Assert.Null(health.ErrorMessage);
    }

    private sealed class ThrowingHealthProvider : BaseGeocodeProvider
    {
        private readonly Exception? _exception;

        public ThrowingHealthProvider(Exception? exception)
        {
            _exception = exception;
        }

        public override string Name => "throwing-health";

        public override GeocodeProviderCapabilities Capabilities => new(SupportsForwardGeocode: true);

        public override Task<IReadOnlyList<GeocodeCandidate>> ForwardGeocodeAsync(
            ForwardGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GeocodeCandidate>>([]);

        public override Task<ReverseGeocodeMatch?> ReverseGeocodeAsync(
            ReverseGeocodeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ReverseGeocodeMatch?>(null);

        protected override Task CheckHealthCoreAsync(CancellationToken cancellationToken = default)
        {
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.CompletedTask;
        }
    }
}
