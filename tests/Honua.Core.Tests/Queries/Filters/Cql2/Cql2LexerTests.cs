// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters.Cql2;
using Xunit;

namespace Honua.Core.Tests.Queries.Filters.Cql2;

public class Cql2LexerTests
{
    [Fact]
    public void Tokenize_SimpleComparison_ReturnsCorrectTokens()
    {
        // Arrange
        var lexer = new Cql2Lexer("name = 'John'");

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens.Should().HaveCount(4); // name, =, 'John', EOF
        tokens[0].Type.Should().Be(Cql2TokenType.Identifier);
        tokens[0].Value.Should().Be("name");
        tokens[1].Type.Should().Be(Cql2TokenType.Equal);
        tokens[1].Value.Should().Be("=");
        tokens[2].Type.Should().Be(Cql2TokenType.Text);
        tokens[2].Value.Should().Be("John");
        tokens[3].Type.Should().Be(Cql2TokenType.EndOfInput);
    }

    [Theory]
    [InlineData("=", Cql2TokenType.Equal)]
    [InlineData("<>", Cql2TokenType.NotEqual)]
    [InlineData("<", Cql2TokenType.LessThan)]
    [InlineData("<=", Cql2TokenType.LessThanOrEqual)]
    [InlineData(">", Cql2TokenType.GreaterThan)]
    [InlineData(">=", Cql2TokenType.GreaterThanOrEqual)]
    public void Tokenize_ComparisonOperators_ReturnsCorrectTokenTypes(string op, Cql2TokenType expectedType)
    {
        // Arrange
        var lexer = new Cql2Lexer($"field {op} value");

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[1].Type.Should().Be(expectedType);
        tokens[1].Value.Should().Be(op);
    }

    [Theory]
    [InlineData("AND", Cql2TokenType.And)]
    [InlineData("OR", Cql2TokenType.Or)]
    [InlineData("NOT", Cql2TokenType.Not)]
    [InlineData("LIKE", Cql2TokenType.Like)]
    [InlineData("IN", Cql2TokenType.In)]
    [InlineData("IS", Cql2TokenType.IsNull)]
    [InlineData("NULL", Cql2TokenType.Null)]
    [InlineData("TRUE", Cql2TokenType.Boolean)]
    [InlineData("FALSE", Cql2TokenType.Boolean)]
    public void Tokenize_Keywords_ReturnsCorrectTokenTypes(string keyword, Cql2TokenType expectedType)
    {
        // Arrange
        var lexer = new Cql2Lexer(keyword);

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(expectedType);
        tokens[0].Value.Should().Be(keyword);
    }

    [Theory]
    [InlineData("S_INTERSECTS", Cql2TokenType.S_Intersects)]
    [InlineData("S_CONTAINS", Cql2TokenType.S_Contains)]
    [InlineData("S_WITHIN", Cql2TokenType.S_Within)]
    [InlineData("S_CROSSES", Cql2TokenType.S_Crosses)]
    [InlineData("S_TOUCHES", Cql2TokenType.S_Touches)]
    [InlineData("S_OVERLAPS", Cql2TokenType.S_Overlaps)]
    [InlineData("S_DISJOINT", Cql2TokenType.S_Disjoint)]
    [InlineData("S_EQUALS", Cql2TokenType.S_Equals)]
    public void Tokenize_SpatialPredicates_ReturnsCorrectTokenTypes(string predicate, Cql2TokenType expectedType)
    {
        // Arrange
        var lexer = new Cql2Lexer(predicate);

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(expectedType);
        tokens[0].Value.Should().Be(predicate);
    }

    [Theory]
    [InlineData("POINT", Cql2TokenType.Point)]
    [InlineData("LINESTRING", Cql2TokenType.LineString)]
    [InlineData("POLYGON", Cql2TokenType.Polygon)]
    [InlineData("MULTIPOINT", Cql2TokenType.MultiPoint)]
    [InlineData("MULTILINESTRING", Cql2TokenType.MultiLineString)]
    [InlineData("MULTIPOLYGON", Cql2TokenType.MultiPolygon)]
    [InlineData("GEOMETRYCOLLECTION", Cql2TokenType.GeometryCollection)]
    public void Tokenize_GeometryTypes_ReturnsCorrectTokenTypes(string geometryType, Cql2TokenType expectedType)
    {
        // Arrange
        var lexer = new Cql2Lexer(geometryType);

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(expectedType);
        tokens[0].Value.Should().Be(geometryType);
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("42.5", "42.5")]
    [InlineData(".5", ".5")]
    [InlineData("0", "0")]
    public void Tokenize_Numbers_ReturnsCorrectTokens(string input, string expectedValue)
    {
        // Arrange
        var lexer = new Cql2Lexer(input);

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(Cql2TokenType.Number);
        tokens[0].Value.Should().Be(expectedValue);
    }

    [Fact]
    public void Tokenize_StringLiteral_HandlesEscapedQuotes()
    {
        // Arrange
        var lexer = new Cql2Lexer("'O''Brien'");

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(Cql2TokenType.Text);
        tokens[0].Value.Should().Be("O'Brien");
    }

    [Fact]
    public void Tokenize_QuotedIdentifier_ReturnsCorrectToken()
    {
        // Arrange
        var lexer = new Cql2Lexer("\"field name\"");

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(Cql2TokenType.Identifier);
        tokens[0].Value.Should().Be("field name");
    }

    [Theory]
    [InlineData("(", Cql2TokenType.LeftParen)]
    [InlineData(")", Cql2TokenType.RightParen)]
    [InlineData(",", Cql2TokenType.Comma)]
    public void Tokenize_Punctuation_ReturnsCorrectTokenTypes(string punctuation, Cql2TokenType expectedType)
    {
        // Arrange
        var lexer = new Cql2Lexer(punctuation);

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(expectedType);
        tokens[0].Value.Should().Be(punctuation);
    }

    [Fact]
    public void Tokenize_WhitespaceHandling_IgnoresWhitespace()
    {
        // Arrange
        var lexer = new Cql2Lexer("  name   =   'John'  ");

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens.Should().HaveCount(4); // name, =, 'John', EOF
        tokens[0].Value.Should().Be("name");
        tokens[1].Value.Should().Be("=");
        tokens[2].Value.Should().Be("John");
    }

    [Fact]
    public void Tokenize_CaseInsensitiveKeywords_ReturnsCorrectTypes()
    {
        // Arrange
        var lexer = new Cql2Lexer("and OR Not like");

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens[0].Type.Should().Be(Cql2TokenType.And);
        tokens[1].Type.Should().Be(Cql2TokenType.Or);
        tokens[2].Type.Should().Be(Cql2TokenType.Not);
        tokens[3].Type.Should().Be(Cql2TokenType.Like);
    }

    [Fact]
    public void Tokenize_ComplexExpression_ReturnsAllTokens()
    {
        // Arrange
        var lexer = new Cql2Lexer("(name LIKE 'John%' AND age >= 18) OR city IN ('Seattle', 'Portland')");

        // Act
        var tokens = lexer.Tokenize();

        // Assert
        tokens.Should().HaveCount(18); // All tokens including EOF

        // Verify key tokens
        tokens[0].Type.Should().Be(Cql2TokenType.LeftParen);
        tokens[1].Type.Should().Be(Cql2TokenType.Identifier);
        tokens[2].Type.Should().Be(Cql2TokenType.Like);
        tokens[4].Type.Should().Be(Cql2TokenType.And);
        tokens[8].Type.Should().Be(Cql2TokenType.RightParen);
        tokens[9].Type.Should().Be(Cql2TokenType.Or);
        tokens[11].Type.Should().Be(Cql2TokenType.In);
    }

    [Theory]
    [InlineData("@")]
    [InlineData("#")]
    [InlineData("$")]
    public void Tokenize_InvalidCharacters_ThrowsArgumentException(string invalidChar)
    {
        // Arrange
        var lexer = new Cql2Lexer($"name = {invalidChar}");

        // Act & Assert
        var action = () => lexer.Tokenize();
        action.Should().Throw<ArgumentException>();
    }
}
