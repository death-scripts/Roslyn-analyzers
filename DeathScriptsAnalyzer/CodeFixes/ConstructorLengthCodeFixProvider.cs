using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace ConstructorLengthAnalyzer;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ConstructorLengthCodeFixProvider)), Shared]
public sealed class ConstructorLengthCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(ConstructorLengthAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var diagnostic = context.Diagnostics.First();
        var span = diagnostic.Location.SourceSpan;

        var parameterList = root?.FindNode(span, getInnermostNodeForTie: true) as ParameterListSyntax;
        if (parameterList is null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Place each parameter on a new line",
                createChangedDocument: c => PutParametersOnNewLinesAsync(context.Document, parameterList, c),
                equivalenceKey: nameof(ConstructorLengthCodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> PutParametersOnNewLinesAsync(Document document, ParameterListSyntax parameterList, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        if (parameterList.Parameters.Count == 0)
            return document;

        var ctor = parameterList.Parent?.FirstAncestorOrSelf<ConstructorDeclarationSyntax>() as ConstructorDeclarationSyntax
                   ?? parameterList.Parent as ConstructorDeclarationSyntax;
        if (ctor is null)
            return document;

        // Compute indentation: current line indent + 4 spaces
        var text = await root.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var ctorLine = text.Lines.GetLineFromPosition(ctor.SpanStart);
        var column = ctor.SpanStart - ctorLine.Start;
        var indent = new string(' ', column + 4);

        // Insert a newline + indent before each parameter
        var updatedParameters = parameterList.Parameters.Select(p =>
        {
            var leading = p.GetLeadingTrivia();
            // Trim leading whitespace only; keep comments if any
            var trimmed = leading.SkipWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToSyntaxTriviaList();
            var newLeading = SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed, SyntaxFactory.Whitespace(indent)).AddRange(trimmed);
            return p.WithLeadingTrivia(newLeading);
        });

        var newParameterList = parameterList
            .WithParameters(SyntaxFactory.SeparatedList(updatedParameters, parameterList.Parameters.GetSeparators()))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.ReplaceNode(parameterList, newParameterList);
        return document.WithSyntaxRoot(newRoot);
    }
}
