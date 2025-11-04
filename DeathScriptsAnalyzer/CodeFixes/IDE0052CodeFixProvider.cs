using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConstructorLengthAnalyzer.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConstructorLengthAnalyzer;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(IDE0052CodeFixProvider)), Shared]
public sealed class IDE0052CodeFixProvider : CodeFixProvider
{
    private const string TargetDiagnosticId = "IDE0052"; // Remove unread private members (built-in)

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(
            TargetDiagnosticId,
            UnreadFieldWithCtorAssignmentAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        var diagnostic = context.Diagnostics.First();
        var span = diagnostic.Location.SourceSpan;

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        var declarator = node as VariableDeclaratorSyntax
            ?? node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();

        if (declarator is null)
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove unread field and related parameter/assignment",
                createChangedDocument: c => RemoveFieldAndAssignmentsAsync(context.Document, declarator, c),
                equivalenceKey: nameof(IDE0052CodeFixProvider)),
            diagnostic);
    }

    private static async Task<Document> RemoveFieldAndAssignmentsAsync(Document document, VariableDeclaratorSyntax declarator, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return document;

        var fieldSymbol = model.GetDeclaredSymbol(declarator, cancellationToken) as IFieldSymbol;
        if (fieldSymbol is null)
            return document;

        var fieldDecl = declarator.FirstAncestorOrSelf<FieldDeclarationSyntax>();
        if (fieldDecl is null)
            return document;

        var typeDecl = fieldDecl.Parent as TypeDeclarationSyntax;
        if (typeDecl is null)
            return document;

        // Annotate the field and specific variable so we can find them after constructor replacements
        var varAnn = new SyntaxAnnotation("var-to-remove");
        var fieldAnn = new SyntaxAnnotation("field-to-remove");
        var annotatedFieldDecl = fieldDecl.ReplaceNode(declarator, declarator.WithAdditionalAnnotations(varAnn))
                                          .WithAdditionalAnnotations(fieldAnn);
        var typeWithAnn = typeDecl.ReplaceNode(fieldDecl, annotatedFieldDecl);

        // Compute constructor updates using the original constructors for semantic queries,
        // but replace the corresponding constructor nodes in the annotated type.
        var origCtors = typeDecl.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        var annCtors = typeWithAnn.Members.OfType<ConstructorDeclarationSyntax>().ToList();

        var ctorMap = new Dictionary<ConstructorDeclarationSyntax, ConstructorDeclarationSyntax>();
        for (int i = 0; i < origCtors.Count; i++)
        {
            var origCtor = origCtors[i];
            var annCtor = annCtors[i];

            var toRemoveStatements = new HashSet<StatementSyntax>();
            var candidateParameters = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
            bool removeExpressionBody = false;

            if (origCtor.Body is not null)
            {
                foreach (var stmt in origCtor.Body.Statements.OfType<ExpressionStatementSyntax>())
                {
                    if (stmt.Expression is AssignmentExpressionSyntax assign && assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        if (IsFieldAccess(assign.Left, fieldSymbol, model))
                        {
                            toRemoveStatements.Add(stmt);
                            if (assign.Right is IdentifierNameSyntax rhsId)
                            {
                                var rhsSym = model.GetSymbolInfo(rhsId, cancellationToken).Symbol as IParameterSymbol;
                                if (rhsSym is not null)
                                    candidateParameters.Add(rhsSym);
                            }
                        }
                    }
                }
            }

            if (origCtor.ExpressionBody is not null)
            {
                var expr = origCtor.ExpressionBody.Expression;
                if (expr is AssignmentExpressionSyntax a && a.IsKind(SyntaxKind.SimpleAssignmentExpression) && IsFieldAccess(a.Left, fieldSymbol, model))
                {
                    removeExpressionBody = true;
                    if (a.Right is IdentifierNameSyntax id)
                    {
                        var rhsSym = model.GetSymbolInfo(id, cancellationToken).Symbol as IParameterSymbol;
                        if (rhsSym is not null)
                            candidateParameters.Add(rhsSym);
                    }
                }
            }

            var updatedCtor = annCtor;
            if (toRemoveStatements.Count > 0 && annCtor.Body is not null)
            {
                // Map statements by index since annCtor/ origCtor bodies are aligned
                var keep = new List<StatementSyntax>();
                for (int s = 0; s < origCtor.Body.Statements.Count; s++)
                {
                    var origStmt = origCtor.Body.Statements[s];
                    var annStmt = annCtor.Body.Statements[s];
                    if (!toRemoveStatements.Contains(origStmt))
                        keep.Add(annStmt);
                }
                updatedCtor = updatedCtor.WithBody(annCtor.Body.WithStatements(SyntaxFactory.List(keep)));
            }

            if (removeExpressionBody)
            {
                updatedCtor = updatedCtor.WithExpressionBody(null).WithSemicolonToken(default).WithBody(updatedCtor.Body ?? SyntaxFactory.Block());
            }

            if (candidateParameters.Count > 0)
            {
                foreach (var param in candidateParameters)
                {
                    var totalUses = CountIdentifierUsagesBoundTo(origCtor, param, model, cancellationToken);
                    var usesInRemovedAssignments = CountUsesInAssignmentsToField(origCtor, param, fieldSymbol, model, cancellationToken);
                    if (totalUses == usesInRemovedAssignments && totalUses > 0)
                    {
                        // Remove parameter from updatedCtor by matching declared symbol on the original parameter
                        var origParamSyntax = origCtor.ParameterList.Parameters
                            .FirstOrDefault(p => (model.GetDeclaredSymbol(p, cancellationToken) as IParameterSymbol)?.Equals(param, SymbolEqualityComparer.Default) == true);
                        if (origParamSyntax is not null)
                        {
                            var index = origCtor.ParameterList.Parameters.IndexOf(origParamSyntax);
                            if (index >= 0 && index < updatedCtor.ParameterList.Parameters.Count)
                            {
                                var newParams = updatedCtor.ParameterList.Parameters.RemoveAt(index);
                                updatedCtor = updatedCtor.WithParameterList(updatedCtor.ParameterList.WithParameters(newParams));
                            }
                        }
                    }
                }
            }

            if (!SyntaxFactory.AreEquivalent(annCtor, updatedCtor))
            {
                ctorMap[annCtor] = updatedCtor;
            }
        }

        var typeAfterCtorEdits = typeWithAnn;
        if (ctorMap.Count > 0)
        {
            typeAfterCtorEdits = typeAfterCtorEdits.ReplaceNodes(ctorMap.Keys, (orig, _) => ctorMap[orig]);
        }

        // Remove the field or just the annotated variable
        var fieldInNew = typeAfterCtorEdits.GetAnnotatedNodes(fieldAnn).OfType<FieldDeclarationSyntax>().FirstOrDefault();
        var varInNew = typeAfterCtorEdits.GetAnnotatedNodes(varAnn).OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        if (fieldInNew is null || varInNew is null)
            return document; // fallback, nothing to change safely

        TypeDeclarationSyntax finalType;
        if (fieldInNew.Declaration.Variables.Count > 1)
        {
            var newVars = fieldInNew.Declaration.Variables.Remove(varInNew);
            var newVarDecl = fieldInNew.Declaration.WithVariables(newVars);
            var newFieldDecl = fieldInNew.WithDeclaration(newVarDecl);
            finalType = typeAfterCtorEdits.ReplaceNode(fieldInNew, newFieldDecl);
        }
        else
        {
            finalType = typeAfterCtorEdits.RemoveNode(fieldInNew, SyntaxRemoveOptions.KeepNoTrivia);
        }

        var newRoot = root.ReplaceNode(typeDecl, finalType);
        return document.WithSyntaxRoot(newRoot);
    }

    private static bool IsFieldAccess(ExpressionSyntax left, IFieldSymbol field, SemanticModel model)
    {
        // Matches: _f or this._f
        if (left is IdentifierNameSyntax id)
        {
            var sym = model.GetSymbolInfo(id).Symbol;
            return SymbolEqualityComparer.Default.Equals(sym, field);
        }
        if (left is MemberAccessExpressionSyntax member && member.IsKind(SyntaxKind.SimpleMemberAccessExpression))
        {
            var sym = model.GetSymbolInfo(member).Symbol;
            return SymbolEqualityComparer.Default.Equals(sym, field);
        }
        return false;
    }

    private static int CountIdentifierUsagesBoundTo(SyntaxNode root, IParameterSymbol parameter, SemanticModel model, CancellationToken ct)
    {
        return root.DescendantNodes()
                   .OfType<IdentifierNameSyntax>()
                   .Select(n => model.GetSymbolInfo(n, ct).Symbol)
                   .Count(s => SymbolEqualityComparer.Default.Equals(s, parameter));
    }

    private static int CountUsesInAssignmentsToField(SyntaxNode root, IParameterSymbol parameter, IFieldSymbol field, SemanticModel model, CancellationToken ct)
    {
        int count = 0;
        foreach (var assign in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                continue;
            if (!IsFieldAccess(assign.Left, field, model))
                continue;
            if (assign.Right is IdentifierNameSyntax id)
            {
                var rhs = model.GetSymbolInfo(id, ct).Symbol;
                if (SymbolEqualityComparer.Default.Equals(rhs, parameter))
                    count++;
            }
        }
        return count;
    }
}
