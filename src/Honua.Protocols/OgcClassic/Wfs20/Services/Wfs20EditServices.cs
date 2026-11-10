// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Classic.Wfs20.Services;

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
