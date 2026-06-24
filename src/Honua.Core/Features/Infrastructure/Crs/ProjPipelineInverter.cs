// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Crs;

/// <summary>
/// Inverts a PROJ pipeline string so a reverse-direction datum transformation
/// (toSrid &#8594; fromSrid) applies the geodetic shift in the correct direction.
/// </summary>
/// <remarks>
/// The datum-transformation catalog stores one authoritative forward PROJ pipeline
/// per source/target pair and synthesizes the reverse direction at load time. A PROJ
/// pipeline is direction-sensitive: a <c>+proj=hgridshift</c> (NADCON/NTv2) step run
/// forward shifts coordinates the opposite way from its inverse. Emitting the forward
/// pipeline for a reverse selection shifts coordinates the wrong way — roughly twice
/// the intended NADCON correction (#2069). The PROJ-canonical inverse of a
/// <c>+proj=pipeline</c> reverses the step order and toggles the <c>+inv</c> flag on
/// each step; an identity <c>+proj=noop</c> is its own inverse.
/// </remarks>
public static class ProjPipelineInverter
{
    private const string PipelinePrefix = "+proj=pipeline";
    private const string StepToken = "+step";
    private const string InvToken = "+inv";

    /// <summary>
    /// Returns the PROJ pipeline string oriented for the requested direction. When
    /// <paramref name="forward"/> is <see langword="true"/> the pipeline is returned
    /// unchanged; otherwise the PROJ-canonical inverse is returned.
    /// </summary>
    /// <param name="pipeline">The forward PROJ pipeline string from the catalog.</param>
    /// <param name="forward">
    /// <see langword="true"/> for the forward (source &#8594; target) direction;
    /// <see langword="false"/> to invert the pipeline.
    /// </param>
    /// <returns>The direction-oriented PROJ pipeline string.</returns>
    public static string Orient(string pipeline, bool forward)
        => forward ? pipeline : Invert(pipeline);

    /// <summary>
    /// Inverts a PROJ pipeline string. Non-pipeline proj strings that carry no
    /// direction-sensitive step (for example the exact-identity <c>+proj=noop</c>) are
    /// returned unchanged; multi-step <c>+proj=pipeline</c> strings have their step
    /// order reversed and the <c>+inv</c> flag toggled on each step.
    /// </summary>
    /// <param name="pipeline">The PROJ pipeline string to invert.</param>
    /// <returns>The inverted PROJ pipeline string.</returns>
    public static string Invert(string pipeline)
    {
        var trimmed = pipeline.Trim();

        // A bare identity is self-inverse; reversing/inv-toggling it is a no-op anyway.
        if (trimmed.Equals("+proj=noop", StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Only +proj=pipeline strings have an unambiguous structural inverse (reverse
        // the steps, toggle +inv). For any other proj string we cannot safely synthesize
        // an inverse here, so return it unchanged rather than emit a wrong-direction guess.
        if (!trimmed.StartsWith(PipelinePrefix, StringComparison.Ordinal))
        {
            return trimmed;
        }

        // Split into the pipeline header (tokens before the first +step) and the steps.
        var steps = new List<List<string>>();
        List<string>? current = null;
        var header = new List<string>();

        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals(StepToken, StringComparison.Ordinal))
            {
                current = new List<string>();
                steps.Add(current);
                continue;
            }

            if (current is null)
            {
                header.Add(token);
            }
            else
            {
                current.Add(token);
            }
        }

        steps.Reverse();

        var result = new List<string>(header);
        foreach (var step in steps)
        {
            result.Add(StepToken);

            // Toggle the +inv flag for this step.
            var hadInv = step.Remove(InvToken);
            result.AddRange(step);
            if (!hadInv)
            {
                result.Add(InvToken);
            }
        }

        return string.Join(' ', result);
    }
}
