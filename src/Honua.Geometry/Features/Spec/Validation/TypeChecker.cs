// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Operators;

namespace Honua.Core.Features.Spec.Validation;

/// <summary>
/// Second validator pass — walks every <see cref="ComputeStep"/>, looks up
/// its operator in the <see cref="IOperatorCatalog"/>, and validates inputs
/// and parameters against the declared <see cref="OperatorSignature"/>.
/// </summary>
internal sealed class TypeChecker
{
    private readonly SpecDocument _document;
    private readonly IOperatorCatalog _operators;
    private readonly IReadOnlyDictionary<string, TypeRef> _locals;
    private readonly List<SpecDiagnostic> _diagnostics = [];

    /// <summary>
    /// Creates a type checker over the given document and catalog.
    /// </summary>
    public TypeChecker(
        SpecDocument document,
        IOperatorCatalog operators,
        IReadOnlyDictionary<string, TypeRef> locals)
    {
        _document = document;
        _operators = operators;
        _locals = locals;
    }

    /// <summary>Gets the diagnostics emitted during <see cref="Check"/>.</summary>
    public IReadOnlyList<SpecDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Walks the document and validates operator invocations.</summary>
    public void Check()
    {
        _diagnostics.Clear();

        foreach (var step in _document.Compute)
        {
            var signature = _operators.Lookup(step.OperatorName);
            if (signature is null)
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.UnknownOperator,
                    $"Unknown operator '{step.OperatorName}'. Registered operators: {string.Join(", ", _operators.OperatorNames)}.",
                    step.OperatorSpan));
                continue;
            }

            CheckInputs(step, signature);
            CheckParameters(step, signature);
        }
    }

    private void CheckInputs(ComputeStep step, OperatorSignature signature)
    {
        var provided = new HashSet<string>(StringComparer.Ordinal);
        if (step.Inputs is not null)
        {
            foreach (var field in step.Inputs.Fields)
            {
                provided.Add(field.Key);
                var port = signature.FindInput(field.Key);
                if (port is null)
                {
                    _diagnostics.Add(SpecDiagnostic.Warning(
                        SpecDiagnosticCode.TypeMismatch,
                        $"Operator '{signature.Name}' does not declare an input port named '{field.Key}'.",
                        field.KeySpan));
                    continue;
                }

                if (field.Value is ReferenceNode reference)
                {
                    if (_locals.TryGetValue(reference.Root, out var referenced))
                    {
                        if (!referenced.IsAssignableTo(port.Type))
                        {
                            _diagnostics.Add(SpecDiagnostic.Error(
                                SpecDiagnosticCode.TypeMismatch,
                                $"Input '{field.Key}' on operator '{signature.Name}' expects {port.Type} but '{reference.Canonical}' resolves to {referenced}.",
                                reference.Span));
                        }
                    }
                }
                else if (field.Value is not null)
                {
                    var literal = InferLiteralType(field.Value);
                    if (!literal.IsAssignableTo(port.Type))
                    {
                        _diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.TypeMismatch,
                            $"Input '{field.Key}' on operator '{signature.Name}' expects {port.Type} but received {literal}.",
                            field.Value.Span));
                    }
                }
            }
        }

        foreach (var port in signature.Inputs)
        {
            if (port.Required && !provided.Contains(port.Name))
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.MissingRequiredParameter,
                    $"Operator '{signature.Name}' requires input '{port.Name}'.",
                    step.Span));
            }
        }
    }

    private void CheckParameters(ComputeStep step, OperatorSignature signature)
    {
        var provided = new HashSet<string>(StringComparer.Ordinal);
        if (step.Parameters is not null)
        {
            foreach (var field in step.Parameters.Fields)
            {
                provided.Add(field.Key);
                var port = signature.FindParameter(field.Key);
                if (port is null)
                {
                    _diagnostics.Add(SpecDiagnostic.Warning(
                        SpecDiagnosticCode.TypeMismatch,
                        $"Operator '{signature.Name}' does not declare a parameter '{field.Key}'.",
                        field.KeySpan));
                    continue;
                }

                var literal = InferLiteralType(field.Value);
                if (!literal.IsAssignableTo(port.Type))
                {
                    _diagnostics.Add(SpecDiagnostic.Error(
                        SpecDiagnosticCode.TypeMismatch,
                        $"Parameter '{field.Key}' on operator '{signature.Name}' expects {port.Type} but received {literal}.",
                        field.Value.Span));
                }
            }
        }

        foreach (var port in signature.Parameters)
        {
            if (port.Required && !provided.Contains(port.Name))
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.MissingRequiredParameter,
                    $"Operator '{signature.Name}' requires parameter '{port.Name}'.",
                    step.Span));
            }
        }
    }

    private static TypeRef InferLiteralType(SpecExpression expression)
        => expression switch
        {
            LiteralNode literal => TypeRef.Intrinsic(literal.Kind),
            GeometryLiteral => TypeRef.Intrinsic(SpecTypeKind.Geometry),
            Cql2Expression => TypeRef.Intrinsic(SpecTypeKind.String),
            ObjectExpression => TypeRef.Intrinsic(SpecTypeKind.Unknown),
            ArrayLiteral => TypeRef.Intrinsic(SpecTypeKind.Unknown),
            _ => TypeRef.Intrinsic(SpecTypeKind.Unknown)
        };
}
