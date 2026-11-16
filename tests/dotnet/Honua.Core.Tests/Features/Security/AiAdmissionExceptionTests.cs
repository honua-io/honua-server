// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Security;

public sealed class AiAdmissionExceptionTests
{
    [UnitTest]
    public void MonthlyActorLimit_PreservesActionableDenialCode()
    {
        new AiAdmissionException("financial_monthly_actor_exceeded").Code
            .Should().Be("financial_monthly_actor_exceeded");
    }
}
