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

namespace ConstructorLengthAnalyzer.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnreadFieldWithCtorAssignmentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "DS0052";

    private const string Category = "Usage";
    private static readonly LocalizableString Description = "Detects private fields that are only assigned in constructors and never read, enabling a code fix to remove the field, assignment, and corresponding constructor parameter.";
    private static readonly LocalizableString Message = "Field is unread; remove field, assignment, and parameter";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    private static readonly LocalizableString Title = "Unread private field assigned from constructor parameter";
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

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

        // Only private instance/static fields declared in source
        if (field.Locations.All(l => !l.IsInSource))
        {
            return;
        }

        if (field.DeclaredAccessibility != Accessibility.Private)
        {
            return;
        }

        INamedTypeSymbol? containingType = field.ContainingType;
        if (containingType is null)
        {
            return;
        }

        // Get a representative syntax node for the containing type
        SyntaxReference? typeDeclRef = containingType.DeclaringSyntaxReferences.FirstOrDefault();
        if (typeDeclRef == null)
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
            // Report at the field identifier(s) in this file
            foreach (SyntaxReference declRef in field.DeclaringSyntaxReferences)
            {
                if (declRef.GetSyntax(context.CancellationToken) is not VariableDeclaratorSyntax syntax)
                {
                    continue;
                }

                Location location = syntax.Identifier.GetLocation();
                context.ReportDiagnostic(Diagnostic.Create(Rule, location));
            }
        }
    }

    private static bool HasAssignmentToField(ConstructorDeclarationSyntax ctor, IFieldSymbol field, SemanticModel model, System.Threading.CancellationToken ct)
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
        if (ctor.ExpressionBody?.Expression is AssignmentExpressionSyntax a && a.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            if (IsFieldAccess(a.Left, field, model, ct))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasNonAssignmentReads(TypeDeclarationSyntax typeDecl, IFieldSymbol field, SemanticModel model, System.Threading.CancellationToken ct)
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
                return true; // found a read-use
            }
        }
        return false;
    }

    private static bool IsFieldAccess(ExpressionSyntax left, IFieldSymbol field, SemanticModel model, System.Threading.CancellationToken ct)
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
        // Checks patterns: _f = x; or this._f = x;
        SyntaxNode? parent = id.Parent;
        if (parent is AssignmentExpressionSyntax a)
        {
            return a.Left == id;
        }
        return parent is MemberAccessExpressionSyntax m && m.Parent is AssignmentExpressionSyntax a2 && a2.Left == m;
    }
}