// ----------------------------------------------------------------------------
// <copyright company="death-scripts">
// Copyright (c) death-scripts. All rights reserved.
// </copyright>
//                   ██████╗ ███████╗ █████╗ ████████╗██╗  ██╗
//                   ██╔══██╗██╔════╝██╔══██╗╚══██╔══╝██║  ██║
//                   ██║  ██║█████╗  ███████║   ██║   ███████║
//                   ██║  ██║██╔══╝  ██╔══██║   ██║   ██╔══██║
//                   ██████╔╝███████╗██║  ██║   ██║   ██║  ██║
//                   ╚═════╝ ╚══════╝╚═╝  ╚═╝   ╚═╝   ╚═╝  ╚═╝
//
//              ███████╗ ██████╗██████╗ ██╗██████╗ ████████╗███████╗
//              ██╔════╝██╔════╝██╔══██╗██║██╔══██╗╚══██╔══╝██╔════╝
//              ███████╗██║     ██████╔╝██║██████╔╝   ██║   ███████╗
//              ╚════██║██║     ██╔══██╗██║██╔═══╝    ██║   ╚════██║
//              ███████║╚██████╗██║  ██║██║██║        ██║   ███████║
//              ╚══════╝ ╚═════╝╚═╝  ╚═╝╚═╝╚═╝        ╚═╝   ╚══════╝
// ----------------------------------------------------------------------------
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeathScriptsAnalyzer.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace DeathScriptsAnalyzer.CodeFixes
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ConstructorLengthCodeFixProvider))]
    [Shared]
    public sealed class ConstructorLengthCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(ConstructorLengthAnalyzer.DiagnosticId);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            Diagnostic diagnostic = context.Diagnostics.First();
            Microsoft.CodeAnalysis.Text.TextSpan span = diagnostic.Location.SourceSpan;

            if (root?.FindNode(span, getInnermostNodeForTie: true) is not ParameterListSyntax parameterList)
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Place each parameter on a new line",
                    createChangedDocument: c => PutParametersOnNewLinesAsync(context.Document, parameterList, c),
                    equivalenceKey: nameof(ConstructorLengthCodeFixProvider)),
                diagnostic);
        }

        private static async Task<Document> PutParametersOnNewLinesAsync(Document document, ParameterListSyntax parameterList, CancellationToken cancellationToken)
        {
            SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                return document;
            }

            if (parameterList.Parameters.Count == 0)
            {
                return document;
            }

            ConstructorDeclarationSyntax? ctor = parameterList.Parent?.FirstAncestorOrSelf<ConstructorDeclarationSyntax>()
                       ?? parameterList.Parent as ConstructorDeclarationSyntax;
            if (ctor is null)
            {
                return document;
            }

            // Compute indentation: current line indent + 4 spaces
            Microsoft.CodeAnalysis.Text.SourceText text = await root.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            Microsoft.CodeAnalysis.Text.TextLine ctorLine = text.Lines.GetLineFromPosition(ctor.SpanStart);
            int column = ctor.SpanStart - ctorLine.Start;
            string indent = new(' ', column + 4);

            // Insert a newline + indent before each parameter
            System.Collections.Generic.IEnumerable<ParameterSyntax> updatedParameters = parameterList.Parameters.Select(p =>
            {
                SyntaxTriviaList leading = p.GetLeadingTrivia();

                // Trim leading whitespace only; keep comments if any
                SyntaxTriviaList trimmed = leading.SkipWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToSyntaxTriviaList();
                SyntaxTriviaList newLeading = SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed, SyntaxFactory.Whitespace(indent)).AddRange(trimmed);
                return p.WithLeadingTrivia(newLeading);
            });

            ParameterListSyntax newParameterList = parameterList
                .WithParameters(SyntaxFactory.SeparatedList(updatedParameters, parameterList.Parameters.GetSeparators()))
                .WithAdditionalAnnotations(Formatter.Annotation);

            SyntaxNode newRoot = root.ReplaceNode(parameterList, newParameterList);
            return document.WithSyntaxRoot(newRoot);
        }
    }
}