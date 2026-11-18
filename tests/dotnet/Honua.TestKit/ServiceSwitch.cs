// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Honua.TestKit;

// Created with DispatchProxy.Create<T, ServiceSwitch<T>>() and registered in place of a service,
// the switch forwards every call to its current target, so one shared host can serve cases that
// each bring their own substitute for that service. The owner serializes those cases and points
// the switch back at its default target between them.
public class ServiceSwitch<T> : DispatchProxy
    where T : class
{
    private T? _target;

    public T Target
    {
        get => _target ?? throw new InvalidOperationException("The service switch has no target.");
        set => _target = value ?? throw new ArgumentNullException(nameof(value));
    }

    // The proxy itself, typed as the service it stands in for.
    public T Service => (T)(object)this;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        try
        {
            return targetMethod.Invoke(Target, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
