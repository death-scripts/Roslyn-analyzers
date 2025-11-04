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
using System.Collections.Generic;
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

namespace DeathScriptsAnalyzer.CodeFixes
{
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
            SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                return;
            }

            Diagnostic diagnostic = context.Diagnostics.First();
            Microsoft.CodeAnalysis.Text.TextSpan span = diagnostic.Location.SourceSpan;

            SyntaxNode node = root.FindNode(span, getInnermostNodeForTie: true);
            VariableDeclaratorSyntax? declarator = node as VariableDeclaratorSyntax
                ?? node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();

            if (declarator is null)
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Remove unread field and related parameter/assignment",
                    createChangedDocument: c => RemoveFieldAndAssignmentsAsync(context.Document, declarator, c),
                    equivalenceKey: nameof(IDE0052CodeFixProvider)),
                diagnostic);
        }

        private static int CountIdentifierUsagesBoundTo(SyntaxNode root, IParameterSymbol parameter, SemanticModel model, CancellationToken ct) => root.DescendantNodes()
                       .OfType<IdentifierNameSyntax>()
                       .Select(n => model.GetSymbolInfo(n, ct).Symbol)
                       .Count(s => SymbolEqualityComparer.Default.Equals(s, parameter));

        private static int CountUsesInAssignmentsToField(SyntaxNode root, IParameterSymbol parameter, IFieldSymbol field, SemanticModel model, CancellationToken ct)
        {
            int count = 0;
            foreach (AssignmentExpressionSyntax assign in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    continue;
                }

                if (!IsFieldAccess(assign.Left, field, model))
                {
                    continue;
                }

                if (assign.Right is IdentifierNameSyntax id)
                {
                    ISymbol? rhs = model.GetSymbolInfo(id, ct).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(rhs, parameter))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static bool IsFieldAccess(ExpressionSyntax left, IFieldSymbol field, SemanticModel model)
        {
            // Matches: _f or this._f
            if (left is IdentifierNameSyntax id)
            {
                ISymbol? sym = model.GetSymbolInfo(id).Symbol;
                return SymbolEqualityComparer.Default.Equals(sym, field);
            }
            if (left is MemberAccessExpressionSyntax member && member.IsKind(SyntaxKind.SimpleMemberAccessExpression))
            {
                ISymbol? sym = model.GetSymbolInfo(member).Symbol;
                return SymbolEqualityComparer.Default.Equals(sym, field);
            }
            return false;
        }

        private static async Task<Document> RemoveFieldAndAssignmentsAsync(Document document, VariableDeclaratorSyntax declarator, CancellationToken cancellationToken)
        {
            SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
            {
                return document;
            }

            if (model.GetDeclaredSymbol(declarator, cancellationToken) is not IFieldSymbol fieldSymbol)
            {
                return document;
            }

            FieldDeclarationSyntax? fieldDecl = declarator.FirstAncestorOrSelf<FieldDeclarationSyntax>();
            if (fieldDecl is null)
            {
                return document;
            }

            if (fieldDecl.Parent is not TypeDeclarationSyntax typeDecl)
            {
                return document;
            }

            // Annotate the field and specific variable so we can find them after constructor replacements
            SyntaxAnnotation varAnn = new("var-to-remove");
            SyntaxAnnotation fieldAnn = new("field-to-remove");
            FieldDeclarationSyntax annotatedFieldDecl = fieldDecl.ReplaceNode(declarator, declarator.WithAdditionalAnnotations(varAnn))
                                              .WithAdditionalAnnotations(fieldAnn);
            TypeDeclarationSyntax typeWithAnn = typeDecl.ReplaceNode(fieldDecl, annotatedFieldDecl);

            // Compute constructor updates using the original constructors for semantic queries,
            // but replace the corresponding constructor nodes in the annotated type.
            List<ConstructorDeclarationSyntax> origCtors = typeDecl.Members.OfType<ConstructorDeclarationSyntax>().ToList();
            List<ConstructorDeclarationSyntax> annCtors = typeWithAnn.Members.OfType<ConstructorDeclarationSyntax>().ToList();

            Dictionary<ConstructorDeclarationSyntax, ConstructorDeclarationSyntax> ctorMap = [];
            for (int i = 0; i < origCtors.Count; i++)
            {
                ConstructorDeclarationSyntax origCtor = origCtors[i];
                ConstructorDeclarationSyntax annCtor = annCtors[i];

                HashSet<StatementSyntax> toRemoveStatements = [];
                HashSet<IParameterSymbol> candidateParameters = new(SymbolEqualityComparer.Default);
                bool removeExpressionBody = false;

                if (origCtor.Body is not null)
                {
                    foreach (ExpressionStatementSyntax stmt in origCtor.Body.Statements.OfType<ExpressionStatementSyntax>())
                    {
                        if (stmt.Expression is AssignmentExpressionSyntax assign && assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                        {
                            if (IsFieldAccess(assign.Left, fieldSymbol, model))
                            {
                                _ = toRemoveStatements.Add(stmt);
                                if (assign.Right is IdentifierNameSyntax rhsId)
                                {
                                    IParameterSymbol? rhsSym = model.GetSymbolInfo(rhsId, cancellationToken).Symbol as IParameterSymbol;
                                    if (rhsSym is not null)
                                    {
                                        _ = candidateParameters.Add(rhsSym);
                                    }
                                }
                            }
                        }
                    }
                }

                if (origCtor.ExpressionBody is not null)
                {
                    ExpressionSyntax expr = origCtor.ExpressionBody.Expression;
                    if (expr is AssignmentExpressionSyntax a && a.IsKind(SyntaxKind.SimpleAssignmentExpression) && IsFieldAccess(a.Left, fieldSymbol, model))
                    {
                        removeExpressionBody = true;
                        if (a.Right is IdentifierNameSyntax id)
                        {
                            IParameterSymbol? rhsSym = model.GetSymbolInfo(id, cancellationToken).Symbol as IParameterSymbol;
                            if (rhsSym is not null)
                            {
                                _ = candidateParameters.Add(rhsSym);
                            }
                        }
                    }
                }

                ConstructorDeclarationSyntax updatedCtor = annCtor;
                if (toRemoveStatements.Count > 0 && annCtor.Body is not null)
                {
                    // Map statements by index since annCtor/ origCtor bodies are aligned
                    List<StatementSyntax> keep = [];
                    for (int s = 0; s < origCtor.Body.Statements.Count; s++)
                    {
                        StatementSyntax origStmt = origCtor.Body.Statements[s];
                        StatementSyntax annStmt = annCtor.Body.Statements[s];
                        if (!toRemoveStatements.Contains(origStmt))
                        {
                            keep.Add(annStmt);
                        }
                    }
                    updatedCtor = updatedCtor.WithBody(annCtor.Body.WithStatements(SyntaxFactory.List(keep)));
                }

                if (removeExpressionBody)
                {
                    updatedCtor = updatedCtor.WithExpressionBody(null).WithSemicolonToken(default).WithBody(updatedCtor.Body ?? SyntaxFactory.Block());
                }

                if (candidateParameters.Count > 0)
                {
                    foreach (IParameterSymbol param in candidateParameters)
                    {
                        int totalUses = CountIdentifierUsagesBoundTo(origCtor, param, model, cancellationToken);
                        int usesInRemovedAssignments = CountUsesInAssignmentsToField(origCtor, param, fieldSymbol, model, cancellationToken);
                        if (totalUses == usesInRemovedAssignments && totalUses > 0)
                        {
                            // Remove parameter from updatedCtor by matching declared symbol on the original parameter
                            ParameterSyntax? origParamSyntax = origCtor.ParameterList.Parameters
                                .FirstOrDefault(p => model.GetDeclaredSymbol(p, cancellationToken)?.Equals(param, SymbolEqualityComparer.Default) == true);
                            if (origParamSyntax is not null)
                            {
                                int index = origCtor.ParameterList.Parameters.IndexOf(origParamSyntax);
                                if (index >= 0 && index < updatedCtor.ParameterList.Parameters.Count)
                                {
                                    SeparatedSyntaxList<ParameterSyntax> newParams = updatedCtor.ParameterList.Parameters.RemoveAt(index);
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

            TypeDeclarationSyntax typeAfterCtorEdits = typeWithAnn;
            if (ctorMap.Count > 0)
            {
                typeAfterCtorEdits = typeAfterCtorEdits.ReplaceNodes(ctorMap.Keys, (orig, _) => ctorMap[orig]);
            }

            // Remove the field or just the annotated variable
            FieldDeclarationSyntax? fieldInNew = typeAfterCtorEdits.GetAnnotatedNodes(fieldAnn).OfType<FieldDeclarationSyntax>().FirstOrDefault();
            VariableDeclaratorSyntax? varInNew = typeAfterCtorEdits.GetAnnotatedNodes(varAnn).OfType<VariableDeclaratorSyntax>().FirstOrDefault();
            if (fieldInNew is null || varInNew is null)
            {
                return document; // fallback, nothing to change safely
            }

            TypeDeclarationSyntax finalType;
            if (fieldInNew.Declaration.Variables.Count > 1)
            {
                SeparatedSyntaxList<VariableDeclaratorSyntax> newVars = fieldInNew.Declaration.Variables.Remove(varInNew);
                VariableDeclarationSyntax newVarDecl = fieldInNew.Declaration.WithVariables(newVars);
                FieldDeclarationSyntax newFieldDecl = fieldInNew.WithDeclaration(newVarDecl);
                finalType = typeAfterCtorEdits.ReplaceNode(fieldInNew, newFieldDecl);
            }
            else
            {
                finalType = typeAfterCtorEdits.RemoveNode(fieldInNew, SyntaxRemoveOptions.KeepNoTrivia);
            }

            SyntaxNode newRoot = root.ReplaceNode(typeDecl, finalType);
            return document.WithSyntaxRoot(newRoot);
        }
    }
}