// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Tests.Features.Admin;

// Closes the content stream once a stream upload finishes, as the S3 SDK does, so code that reads
// a stream after handing it to storage fails here as it does on S3.
public class ClosingUploadStorage : DispatchProxy
{
    private ICloudFileStorage _inner = null!;

    public static ICloudFileStorage Wrap(ICloudFileStorage inner)
    {
        var proxy = Create<ICloudFileStorage, ClosingUploadStorage>();
        ((ClosingUploadStorage)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var result = InvokeInner(targetMethod, args);
        return args is [FileUploadRequest request, ..] && result is Task<UploadResult> upload
            ? CloseAfterAsync(upload, request.Content)
            : result;
    }

    private object? InvokeInner(MethodInfo targetMethod, object?[]? args)
    {
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static async Task<UploadResult> CloseAfterAsync(Task<UploadResult> upload, Stream content)
    {
        try
        {
            return await upload.ConfigureAwait(false);
        }
        finally
        {
            await content.DisposeAsync().ConfigureAwait(false);
        }
    }
}
