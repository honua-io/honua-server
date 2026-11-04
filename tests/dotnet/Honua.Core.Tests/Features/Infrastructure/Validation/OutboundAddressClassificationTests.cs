// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Honua.Tests.OutboundSecurity;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public sealed class OutboundAddressClassificationTests
{
    [UnitTheory]
    [MemberData(nameof(OutboundAddressCases.Disallowed), MemberType = typeof(OutboundAddressCases))]
    public void IsPrivateOrReservedAddress_DisallowedRange_ReturnsTrue(string address, string range)
        => OutboundHttpUrlValidator.IsPrivateOrReservedAddress(IPAddress.Parse(address)).Should().BeTrue(range);

    [UnitTheory]
    [MemberData(nameof(OutboundAddressCases.Allowed), MemberType = typeof(OutboundAddressCases))]
    public void IsPrivateOrReservedAddress_PublicAddress_ReturnsFalse(string address, string range)
        => OutboundHttpUrlValidator.IsPrivateOrReservedAddress(IPAddress.Parse(address)).Should().BeFalse(range);
}
