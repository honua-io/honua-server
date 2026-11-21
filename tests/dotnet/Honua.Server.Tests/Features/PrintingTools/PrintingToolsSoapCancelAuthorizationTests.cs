// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.PrintingTools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.PrintingTools;

[Protocol(TestProtocols.PrintingTools)]
public sealed class PrintingToolsSoapCancelAuthorizationTests
{
    [UnitTest]
    public void CancelJob_BySubmitter_IsAllowed()
        => CanCancel(OwnedJob(), User("user-1")).Should().BeTrue();

    [UnitTest]
    public void CancelJob_ByAnotherUser_IsRefused()
        => CanCancel(OwnedJob(), User("user-2")).Should().BeFalse();

    [UnitTest]
    public void CancelJob_ByAdministrator_IsAllowed()
        => CanCancel(OwnedJob(), Administrator("user-2")).Should().BeTrue();

    [UnitTest]
    public void CancelAnonymousJob_ByAnonymousCaller_IsRefused()
        => CanCancel(AnonymousJob(), new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse();

    [UnitTest]
    public void CancelAnonymousJob_ByAuthenticatedUser_IsRefused()
        => CanCancel(AnonymousJob(), User("user-2")).Should().BeFalse();

    [UnitTest]
    public void CancelAnonymousJob_ByAdministrator_IsAllowed()
        => CanCancel(AnonymousJob(), Administrator("user-2")).Should().BeTrue();

    private static PrintProgress OwnedJob() => Job() with
    {
        AnonymousSubmission = false,
        RequestedBy = Invoke<string?>("ResolveOwner", User("user-1")),
    };

    private static PrintProgress AnonymousJob() => Job() with { AnonymousSubmission = true };

    private static PrintProgress Job() => new()
    {
        JobId = "job-1",
        Format = "PNG32",
        TemplateName = "MAP_ONLY",
        Status = OperationStatus.Queued,
    };

    private static ClaimsPrincipal User(string id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private static ClaimsPrincipal Administrator(string id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, "admin")], "test"));

    private static bool CanCancel(PrintProgress job, ClaimsPrincipal caller)
        => Invoke<bool>("CanCancelJob", job, new DefaultHttpContext { User = caller });

    private static T Invoke<T>(string name, params object[] arguments)
        => (T)typeof(PrintingToolsEndpoints).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!;
}
