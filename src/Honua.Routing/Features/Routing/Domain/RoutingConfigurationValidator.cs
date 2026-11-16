// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Routing.Features.Routing.Domain;

public sealed class RoutingConfigurationValidator : IValidateOptions<RoutingConfiguration>
{
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

        if (options.MaxBarriers <= 0)
        {
            errors.Add("Routing:MaxBarriers must be greater than 0.");
        }

        if (options.MaxIncidents <= 0)
        {
            errors.Add("Routing:MaxIncidents must be greater than 0.");
        }

        if (options.MaxClosestFacilities <= 0)
        {
            errors.Add("Routing:MaxClosestFacilities must be greater than 0.");
        }

        if (options.MaxOrigins <= 0)
        {
            errors.Add("Routing:MaxOrigins must be greater than 0.");
        }

        if (options.MaxDestinations <= 0)
        {
            errors.Add("Routing:MaxDestinations must be greater than 0.");
        }

        if (options.MaxLocationAllocationFacilities <= 0)
        {
            errors.Add("Routing:MaxLocationAllocationFacilities must be greater than 0.");
        }

        if (options.MaxDemandPoints <= 0)
        {
            errors.Add("Routing:MaxDemandPoints must be greater than 0.");
        }

        if (options.MaxSolvePairs <= 0)
        {
            errors.Add("Routing:MaxSolvePairs must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(options.NetworkDatasetId))
        {
            errors.Add("Routing:NetworkDatasetId is required.");
        }

        if (!Enum.IsDefined(options.CostUnit))
        {
            errors.Add(
                "Routing:CostUnit must be one of Minutes, Seconds, or Hours (the declared physical unit of the topology cost/reverse_cost weight).");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
