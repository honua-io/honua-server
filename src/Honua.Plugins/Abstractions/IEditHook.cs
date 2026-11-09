// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

// An after-hook failure must not roll back an edit that is already committed.
public interface IEditHook
{
    ValueTask<EditHookResult> OnBeforeEditAsync(EditHookContext context, CancellationToken cancellationToken);

    ValueTask OnAfterEditAsync(EditHookContext context, CancellationToken cancellationToken);
}
