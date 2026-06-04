// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Validator for <see cref="RoutingConfiguration"/>. Mirrors the shape of the
/// geocoding configuration validator but is self-contained (implements
/// <see cref="IValidateOptions{TOptions}"/> directly) so the routing satellite
/// keeps its minimal dependency surface (Abstractions only — no back-reference to
/// <c>Honua.Core</c>).
/// </summary>
public sealed class RoutingConfigurationValidator : IValidateOptions<RoutingConfiguration>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RoutingConfiguration options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Provider))
        {
            errors.Add("Routing:Provider is required.");
        }

        if (options.MaxStops <= 1)
        {
            errors.Add("Routing:MaxStops must be greater than 1 (a route needs at least two stops).");
        }

        if (options.MaxFacilities <= 0)
        {
            errors.Add("Routing:MaxFacilities must be greater than 0.");
        }

        if (options.MaxBreaks <= 0)
        {
            errors.Add("Routing:MaxBreaks must be greater than 0.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
