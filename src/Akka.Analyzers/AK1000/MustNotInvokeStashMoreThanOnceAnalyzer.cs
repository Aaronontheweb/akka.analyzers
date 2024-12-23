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

        // For lambdas, namely Receive<T> and ReceiveAny
        context.RegisterSyntaxNodeAction(ctx =>
        {
            var node = ctx.Node;
            var semanticModel = ctx.SemanticModel;
            var akkaCore = akkaContext.AkkaCore;
            var stashMethod = akkaCore.Actor.IStash.Stash!;
            var foundDuplicates = false;

            // First: need to check if this method / lambda is declared in an ActorBase subclass
            var symbol = semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is null || !symbol.ContainingType.IsActorBaseSubclass(akkaContext.AkkaCore))
                return;

            // Find all stash calls within the method or lambda
            var stashCalls = node.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(c => IsStashInvocation(semanticModel, c, stashMethod))
                .ToList();

            // If there are less than 2 stash calls, we can skip the rest of the analysis
            if (stashCalls.Count < 2)
                return;

            // Flag all stash calls if they are in the same block without branching
            var sameScopeCalls = stashCalls
                .GroupBy(GetContainingBlock)
                .Where(group => group.Key != null).ToArray();

            foreach (var group in sameScopeCalls)
            {
                var callsInBlock = group.ToList();
                // simple / dumb check - are there multiple calls to Stash in the same block?
                if (callsInBlock.Count > 1)
                {
                    foundDuplicates = true;
                    goto FoundProblems;
                }
            }

            // if sameScopeCalls == all the duplicates, skip the depth analysis
            if (sameScopeCalls.Sum(c => c.Count()) == stashCalls.Count)
                return;
            
            
            var blockSyntax = node as BlockSyntax ?? node.ChildNodes().OfType<BlockSyntax>().FirstOrDefault();
            if (blockSyntax == null)
                return;

            var controlFlowAnalysis = semanticModel.AnalyzeControlFlow(blockSyntax);
            if (controlFlowAnalysis is not { Succeeded: true })
                return;
            
            // Now analyze control flow for mutually exclusive paths
            var reachableCalls = stashCalls
                .Where(call => controlFlowAnalysis.EntryPoints
                    .Contains(call.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault()!)).ToList();


            if (reachableCalls.Count > 1)
            {
                foundDuplicates = true;
            }

            FoundProblems:
                if (!foundDuplicates) return;
                {
                    foreach (var call in stashCalls)
                    {
                        ReportDiagnostic(call, ctx);
                    }
                }
        }, SyntaxKind.MethodDeclaration, SyntaxKind.InvocationExpression);
        return;

        static void ReportDiagnostic(InvocationExpressionSyntax call, SyntaxNodeAnalysisContext ctx)
        {
            var diagnostic = Diagnostic.Create(
                descriptor: RuleDescriptors.Ak1008MustNotInvokeStashMoreThanOnce,
                location: call.GetLocation());
            ctx.ReportDiagnostic(diagnostic);
        }
    }

    private static bool IsStashInvocation(SemanticModel model, InvocationExpressionSyntax invocation,
        IMethodSymbol stashMethod)
    {
        var symbol = model.GetSymbolInfo(invocation).Symbol;
        return SymbolEqualityComparer.Default.Equals(symbol, stashMethod);
    }

    private static BlockSyntax? GetContainingBlock(SyntaxNode node)
    {
        return node.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
    }
}