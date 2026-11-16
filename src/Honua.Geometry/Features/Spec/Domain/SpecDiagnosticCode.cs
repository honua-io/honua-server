// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public enum SpecDiagnosticCode
{
    None = 0,

    SyntaxError,

    ParseError,

    DuplicateIdentifier,

    MissingSection,

    UnknownReference,

    UnknownOperator,

    TypeMismatch,

    MissingRequiredParameter,

    CrsUnitMismatch,

    UnsupportedGrammarVersion,

    CapabilityVersionDrift,

    CatalogUnavailable
}
