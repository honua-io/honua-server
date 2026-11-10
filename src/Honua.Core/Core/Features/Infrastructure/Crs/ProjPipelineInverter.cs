// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Crs;

public static class ProjPipelineInverter
{
    private const string PipelinePrefix = "+proj=pipeline";
    private const string StepToken = "+step";
    private const string InvToken = "+inv";

    public static string Orient(string pipeline, bool forward)
        => forward ? pipeline : Invert(pipeline);

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
