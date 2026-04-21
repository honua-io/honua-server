// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Server.Features.Grounding;

/// <summary>
/// Validates <see cref="GroundingOptions"/> at startup so misconfigured
/// thresholds fail fast instead of silently corrupting banding, capping, or
/// ambiguity evaluation. Pairs with <c>.ValidateOnStart()</c> on the options
/// registration.
/// </summary>
internal sealed class GroundingOptionsValidator : ConfigurationValidator<GroundingOptions>
{
    protected override void PerformFeatureSpecificValidation(GroundingOptions options, List<string> errors)
    {
        // A non-positive cap breaks the ApplyBandsAndCap capacity hint and
        // would emit no candidates even when the ranker has good matches.
        ValidateRange(options.MaxCandidatesPerKind, 1, 1000, nameof(GroundingOptions.MaxCandidatesPerKind), errors);

        ValidateRange(options.WorkflowFamilyFloor, 0.0, 1.0, nameof(GroundingOptions.WorkflowFamilyFloor), errors);
        ValidateRange(options.HighConfidenceFloor, 0.0, 1.0, nameof(GroundingOptions.HighConfidenceFloor), errors);
        ValidateRange(options.MediumConfidenceFloor, 0.0, 1.0, nameof(GroundingOptions.MediumConfidenceFloor), errors);
        ValidateRange(options.MaterialSpread, 0.0, 1.0, nameof(GroundingOptions.MaterialSpread), errors);

        // Medium must not exceed High — otherwise BandFor would never return
        // Medium because the High branch would always match first.
        ValidateLogicalOrder(
            options.MediumConfidenceFloor,
            options.HighConfidenceFloor,
            nameof(GroundingOptions.MediumConfidenceFloor),
            nameof(GroundingOptions.HighConfidenceFloor),
            errors);
    }
}
