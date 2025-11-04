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
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DeathScriptsAnalyzer.Analyzers
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class UnreadFieldWithCtorAssignmentAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "DS0052";
        private const string Category = "Usage";

        private const string DescriptionText =
            "Detects private fields that are only assigned in constructors and never read, enabling a code fix to remove the field, assignment, and corresponding constructor parameter.";

        private const string MessageText = "Field is unread; remove field, assignment, and parameter";
        private const string TitleText = "Unread private field assigned from constructor parameter";
        private static readonly DiagnosticDescriptor Rule;

        // Build the descriptor in a static ctor so member reordering can't break it
        static UnreadFieldWithCtorAssignmentAnalyzer() => Rule = new DiagnosticDescriptor(
                id: DiagnosticId,
                title: TitleText,
                messageFormat: MessageText,
                category: Category,
                defaultSeverity: DiagnosticSeverity.Warning,
                isEnabledByDefault: true,
                description: DescriptionText);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
                => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
        }

        private static void AnalyzeField(SymbolAnalysisContext context)
        {
            if (context.Symbol is not IFieldSymbol field)
            {
                return;
            }

            // Only private fields declared in source
            if (field.DeclaredAccessibility != Accessibility.Private)
            {
                return;
            }

            if (field.Locations.All(l => !l.IsInSource))
            {
                return;
            }

            INamedTypeSymbol? containingType = field.ContainingType;
            if (containingType is null)
            {
                return;
            }

            SyntaxReference? typeDeclRef = containingType.DeclaringSyntaxReferences.FirstOrDefault();
            if (typeDeclRef is null)
            {
                return;
            }

            if (typeDeclRef.GetSyntax(context.CancellationToken) is not TypeDeclarationSyntax typeNode)
            {
                return;
            }

            SyntaxTree tree = typeNode.SyntaxTree;
            SemanticModel model = context.Compilation.GetSemanticModel(tree);

            ImmutableArray<ConstructorDeclarationSyntax> constructors = typeNode.Members.OfType<ConstructorDeclarationSyntax>().ToImmutableArray();
            if (constructors.IsDefaultOrEmpty)
            {
                return;
            }

            // Must be assigned in at least one constructor
            bool assignedInCtor = constructors.Any(ctor => HasAssignmentToField(ctor, field, model, context.CancellationToken));
            if (!assignedInCtor)
            {
                return;
            }

            // Must have no reads anywhere in the type
            bool hasReads = HasNonAssignmentReads(typeNode, field, model, context.CancellationToken);
            if (!hasReads)
            {
                // Report at each field identifier
                foreach (SyntaxReference declRef in field.DeclaringSyntaxReferences)
                {
                    if (declRef.GetSyntax(context.CancellationToken) is VariableDeclaratorSyntax v)
                    {
                        Location location = v.Identifier.GetLocation();
                        context.ReportDiagnostic(Diagnostic.Create(Rule, location));
                    }
                }
            }
        }

        private static bool HasAssignmentToField(
            ConstructorDeclarationSyntax ctor,
            IFieldSymbol field,
            SemanticModel model,
            System.Threading.CancellationToken ct)
        {
            // Block-bodied assignments
            if (ctor.Body is not null)
            {
                foreach (AssignmentExpressionSyntax assign in ctor.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
                    {
                        continue;
                    }

                    if (IsFieldAccess(assign.Left, field, model, ct))
                    {
                        return true;
                    }
                }
            }

            // Expression-bodied assignment
            return ctor.ExpressionBody?.Expression is AssignmentExpressionSyntax a &&
                    a.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                    IsFieldAccess(a.Left, field, model, ct);
        }

        private static bool HasNonAssignmentReads(
                TypeDeclarationSyntax typeDecl,
                IFieldSymbol field,
                SemanticModel model,
                System.Threading.CancellationToken ct)
        {
            foreach (IdentifierNameSyntax id in typeDecl.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                ISymbol? sym = model.GetSymbolInfo(id, ct).Symbol;
                if (!SymbolEqualityComparer.Default.Equals(sym, field))
                {
                    continue;
                }

                if (!IsOnAssignmentLeft(id))
                {
                    return true; // found a read
                }
            }

            return false;
        }

        private static bool IsFieldAccess(
            ExpressionSyntax left,
            IFieldSymbol field,
            SemanticModel model,
            System.Threading.CancellationToken ct)
        {
            if (left is IdentifierNameSyntax id)
            {
                ISymbol? sym = model.GetSymbolInfo(id, ct).Symbol;
                return SymbolEqualityComparer.Default.Equals(sym, field);
            }

            if (left is MemberAccessExpressionSyntax member)
            {
                ISymbol? sym = model.GetSymbolInfo(member, ct).Symbol;
                return SymbolEqualityComparer.Default.Equals(sym, field);
            }

            return false;
        }

        private static bool IsOnAssignmentLeft(IdentifierNameSyntax id)
        {
            // Matches: _f = x; or this._f = x;
            SyntaxNode? parent = id.Parent;
            if (parent is AssignmentExpressionSyntax a)
            {
                return a.Left == id;
            }

            return parent is MemberAccessExpressionSyntax m && m.Parent is AssignmentExpressionSyntax a2 && a2.Left == m;
        }
    }
}