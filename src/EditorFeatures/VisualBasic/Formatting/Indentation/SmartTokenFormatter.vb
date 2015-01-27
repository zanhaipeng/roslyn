' Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

Imports System.Threading
Imports Microsoft.CodeAnalysis.Editor.Implementation.Formatting.Indentation
Imports Microsoft.CodeAnalysis.Formatting
Imports Microsoft.CodeAnalysis.Formatting.Rules
Imports Microsoft.CodeAnalysis.Options
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.Formatting.Indentation
    Friend Class SmartTokenFormatter
        Implements ISmartTokenFormatter

        Private ReadOnly optionSet As OptionSet
        Private ReadOnly formattingRules As IEnumerable(Of IFormattingRule)

        Private ReadOnly root As CompilationUnitSyntax

        Public Sub New(optionSet As OptionSet,
                       formattingRules As IEnumerable(Of IFormattingRule),
                       root As CompilationUnitSyntax)
            Contract.ThrowIfNull(optionSet)
            Contract.ThrowIfNull(formattingRules)
            Contract.ThrowIfNull(root)

            Me.optionSet = optionSet
            Me.formattingRules = formattingRules

            Me.root = root
        End Sub

        Public Function FormatToken(workspace As Workspace, token As SyntaxToken, cancellationToken As CancellationToken) As IList(Of TextChange) Implements ISmartTokenFormatter.FormatToken
            Contract.ThrowIfTrue(token.Kind = SyntaxKind.None OrElse token.Kind = SyntaxKind.EndOfFileToken)

            ' get previous token
            Dim previousToken = token.GetPreviousToken()

            Return Formatter.GetFormattedTextChanges(Me.root, TextSpan.FromBounds(previousToken.SpanStart, token.Span.End), workspace, Me.optionSet, Me.formattingRules, cancellationToken)
        End Function
    End Class
End Namespace
