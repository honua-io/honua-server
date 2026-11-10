// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

public static class CapabilityReasonCodes
{
    public const string NotRegistered = "capability-not-registered";

    public const string NotImplemented = "capability-not-implemented";

    public const string ExperimentalDisabled = "experimental-disabled";

    public const string DisabledByConfiguration = "disabled-by-configuration";

    public const string ProtocolProfileExcluded = "protocol-profile-excluded";

    // An edition failure uses the same reason as the capability manifest so the experimental flag cannot mask an entitlement failure.
    public const string LicenseRequired = "license-required";
}
