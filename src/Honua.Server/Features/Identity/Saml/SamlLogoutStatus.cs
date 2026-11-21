// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Identity.Saml;

// The outcome the SP reports to the IdP in its LogoutResponse.
internal enum SamlLogoutStatus
{
    Success,
    PartialLogout,
}

// The result of ending the sessions a logout request names: a rejection to return, or the status to report.
internal readonly record struct SamlLogoutAttempt(IResult? Rejection, SamlLogoutStatus Status)
{
    public static SamlLogoutAttempt Rejected(IResult rejection) => new(rejection, SamlLogoutStatus.PartialLogout);
}
