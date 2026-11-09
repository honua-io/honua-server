// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Core.Features.Mobile.FieldCollection.Abstractions;

public interface IFieldCollectionAutomationStore
{
    Task<IReadOnlyList<FieldCollectionAutomationAction>> GetEnabledActionsAsync(
        int layerId,
        CancellationToken cancellationToken = default);
}
