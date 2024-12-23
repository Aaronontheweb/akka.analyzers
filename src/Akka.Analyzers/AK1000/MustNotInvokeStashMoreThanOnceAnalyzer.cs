// -----------------------------------------------------------------------
//  <copyright file="MustNotInvokeStashMoreThanOnce.cs" company="Akka.NET Project">
//      Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using Akka.Analyzers.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Akka.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class MustNotInvokeStashMoreThanOnceAnalyzer()
    : AkkaDiagnosticAnalyzer(RuleDescriptors.Ak1008MustNotInvokeStashMoreThanOnce)
{
    public override void AnalyzeCompilation(CompilationStartAnalysisContext context, AkkaContext akkaContext)
    {
        Guard.AssertIsNotNull(context);
        Guard.AssertIsNotNull(akkaContext);

        context.RegisterSyntaxNodeAction(ctx => AnalyzeMethod(ctx, akkaContext), SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context, AkkaContext akkaContext)
    {
        var semanticModel = context.SemanticModel;
        var methodDeclaration = (MethodDeclarationSyntax)context.Node;
        
        // SymbolEqualityComparer.Default.Equals(invocation.TargetMethod, stashMethod)
        
        // First: need to check if this method is declared in an ActorBase subclass
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration);
        if (methodSymbol is null || !methodSymbol.ContainingType.IsActorBaseSubclass(akkaContext.AkkaCore))
            return;

        var stashMethod = akkaContext.AkkaCore.Actor.IStash.Stash!;
        
         // Find all "Stash.Stash()" calls in the method
        var stashCalls = methodDeclaration.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => IsStashInvocation(semanticModel, invocation, stashMethod));
        
        // Group calls by their parent block
        var callsGroupedByBlock = stashCalls
            .GroupBy(GetContainingBlock)
            .Where(group => group.Key != null);
        
        foreach (var group in callsGroupedByBlock)
        {
            var callsInBlock = group.ToList();

            // can't be null - we check for that on line 47
            if (callsInBlock.Count > 1 && !AreCallsSeparatedByConditionals(group.Key!, callsInBlock))
            {
                // we could skip the first stash call here, but since we don't know _which_ call is the offending
                // duplicate, we'll just report all of them
                foreach (var stashCall in callsInBlock)
                {
                    var diagnostic = Diagnostic.Create(
                        descriptor:RuleDescriptors.Ak1008MustNotInvokeStashMoreThanOnce,
                        location:stashCall.GetLocation());
                    context.ReportDiagnostic(diagnostic);
                }
            }
        }
    }
    
    private static bool IsStashInvocation(SemanticModel model, InvocationExpressionSyntax invocation, IMethodSymbol stashMethod)
    {
        var symbol = model.GetSymbolInfo(invocation).Symbol;
        return SymbolEqualityComparer.Default.Equals(symbol, stashMethod);
    }
    
    private static BlockSyntax? GetContainingBlock(SyntaxNode node)
    {
        return node.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
    }
    
    private static bool AreCallsSeparatedByConditionals(BlockSyntax block, List<InvocationExpressionSyntax> calls)
    {
        var conditionals = block.DescendantNodes().OfType<IfStatementSyntax>().ToList();

        foreach (var call in calls)
        {
            bool isInConditional = conditionals.Any(ifStatement => ifStatement.Contains(call));

            if (!isInConditional)
            {
                return false;
            }
        }

        return true;
    }
}

