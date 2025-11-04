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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ConstructorLengthAnalyzer.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConstructorLengthAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "DS0001";

    private const string Category = "Formatting";
    private static readonly LocalizableString Description = "When a constructor declaration line exceeds 100 characters, parameters should be placed on new lines.";
    private static readonly LocalizableString Message = "Constructor signature exceeds 100 characters";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        Message,
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: Description);

    private static readonly LocalizableString Title = "Constructor signature exceeds 100 characters";
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ConstructorDeclaration);
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        ConstructorDeclarationSyntax decl = (ConstructorDeclarationSyntax)context.Node;
        if (decl.ParameterList is null || decl.ParameterList.Parameters.Count == 0)
        {
            return;
        }

        SyntaxTree tree = decl.SyntaxTree;
        Microsoft.CodeAnalysis.Text.SourceText text = tree.GetText(context.CancellationToken);

        // Measure the length of the first line where the constructor starts.
        int start = decl.GetFirstToken(includeZeroWidth: true).SpanStart;
        Microsoft.CodeAnalysis.Text.TextLine startLine = text.Lines.GetLineFromPosition(start);
        string lineText = startLine.ToString();

        if (lineText.Length > 100)
        {
            // Report on the parameter list to guide the fix.
            Location location = decl.ParameterList.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Rule, location));
        }
    }
}