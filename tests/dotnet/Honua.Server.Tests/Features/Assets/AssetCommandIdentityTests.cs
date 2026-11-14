// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Assets;
using Honua.Server.Features.Assets;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Assets;

public sealed class AssetCommandIdentityTests
{
    [UnitTheory]
    [InlineData("Bearer", "issuer-b")]
    [InlineData("OtherBearer", "issuer-a")]
    public void Command_SameSubjectFromAnotherAuthority_IsNotAReplay(string scheme, string issuer)
    {
        var request = new WorkTaskTransitionRequest { CommandId = Guid.NewGuid(), ExpectedVersion = 1, Action = WorkTaskAction.Start };
        var subject = new CommandSubject(request.CommandId!.Value, "service", (WorkTaskAction.Start, Guid.NewGuid()));
        var original = AssetCommandHttp.CreateCommand(Context("Bearer", "issuer-a"), subject,
            (request, AssetsJsonContext.Default.WorkTaskTransitionRequest));
        var other = AssetCommandHttp.CreateCommand(Context(scheme, issuer), subject,
            (request, AssetsJsonContext.Default.WorkTaskTransitionRequest));

        other.Actor.Should().NotBe(original.Actor);
        other.RequestSha256.Should().NotBe(original.RequestSha256);
        AssetCommandHttp.ResolveTaskActor(Context(scheme, issuer)).Id.Should().NotBe(original.Actor);
    }

    private static DefaultHttpContext Context(string scheme, string issuer) => new()
    {
        RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider(),
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "same-subject"), new Claim("iss", issuer)], scheme)),
    };
}
