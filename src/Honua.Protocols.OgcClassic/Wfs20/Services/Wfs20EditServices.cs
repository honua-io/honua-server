// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

/// <summary>
/// Feature-local facade that groups the shared edit/transaction collaborators used by the WFS handler.
/// Paired with <see cref="Wfs20QueryServices"/> and <see cref="Wfs20SpatialServices"/> so the handler
/// composes a small number of cohesive facades instead of one large aggregate, without changing behavior.
/// </summary>
internal sealed class Wfs20EditServices(
    IFeatureWriter featureWriter,
    Wfs20EditParameterAdapter editParameterAdapter,
    IEditProcessor editProcessor,
    FeatureMutationValidator mutationValidator,
    FeatureMutationEventService mutationEventService,
    IOptions<LimitsOptions> limitsOptions)
{
    internal IFeatureWriter FeatureWriter { get; } = featureWriter;

    internal Wfs20EditParameterAdapter EditParameterAdapter { get; } = editParameterAdapter;

    internal IEditProcessor EditProcessor { get; } = editProcessor;

    internal FeatureMutationValidator MutationValidator { get; } = mutationValidator;

    internal FeatureMutationEventService MutationEventService { get; } = mutationEventService;

    internal EditLimits EditLimits { get; } = limitsOptions?.Value?.Edits ?? throw new ArgumentNullException(nameof(limitsOptions));
}
