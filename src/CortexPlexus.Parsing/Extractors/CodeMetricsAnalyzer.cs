using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Extractors;

/// <summary>
/// Calculates code complexity metrics for a method body:
/// - Cyclomatic complexity: number of decision points + 1
/// - Max nesting depth: deepest level of nested blocks
/// - Line count: EndLine - StartLine + 1
/// </summary>
internal static class CodeMetricsAnalyzer
{
    public static (int CyclomaticComplexity, int MaxNestingDepth) Analyze(SyntaxNode methodBody)
    {
        var complexity = 1; // Base complexity
        var maxDepth = 0;

        var walker = new MetricsWalker();
        walker.Visit(methodBody);

        complexity += walker.DecisionPoints;
        maxDepth = walker.MaxNestingDepth;

        return (complexity, maxDepth);
    }

    private sealed class MetricsWalker : CSharpSyntaxWalker
    {
        public int DecisionPoints { get; private set; }
        public int MaxNestingDepth { get; private set; }

        private int _currentDepth;

        // --- Decision points (each adds 1 to cyclomatic complexity) ---

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            DecisionPoints++;
            EnterBlock();
            base.VisitIfStatement(node);
            ExitBlock();
        }

        public override void VisitElseClause(ElseClauseSyntax node)
        {
            // else-if counts as another decision; plain else does not
            if (node.Statement is IfStatementSyntax)
                DecisionPoints++;

            base.VisitElseClause(node);
        }

        public override void VisitForStatement(ForStatementSyntax node)
        {
            DecisionPoints++;
            EnterBlock();
            base.VisitForStatement(node);
            ExitBlock();
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            DecisionPoints++;
            EnterBlock();
            base.VisitForEachStatement(node);
            ExitBlock();
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            DecisionPoints++;
            EnterBlock();
            base.VisitWhileStatement(node);
            ExitBlock();
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            DecisionPoints++;
            EnterBlock();
            base.VisitDoStatement(node);
            ExitBlock();
        }

        public override void VisitSwitchSection(SwitchSectionSyntax node)
        {
            // Each case/default adds a decision point
            DecisionPoints++;
            base.VisitSwitchSection(node);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            EnterBlock();
            base.VisitSwitchStatement(node);
            ExitBlock();
        }

        public override void VisitCatchClause(CatchClauseSyntax node)
        {
            DecisionPoints++;
            EnterBlock();
            base.VisitCatchClause(node);
            ExitBlock();
        }

        public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            // Ternary operator: condition ? a : b
            DecisionPoints++;
            base.VisitConditionalExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            // && and || each add a decision point
            if (node.IsKind(SyntaxKind.LogicalAndExpression) ||
                node.IsKind(SyntaxKind.LogicalOrExpression) ||
                node.IsKind(SyntaxKind.CoalesceExpression))
            {
                DecisionPoints++;
            }

            base.VisitBinaryExpression(node);
        }

        public override void VisitSwitchExpression(SwitchExpressionSyntax node)
        {
            // Switch expression arms (pattern matching)
            DecisionPoints += node.Arms.Count;
            EnterBlock();
            base.VisitSwitchExpression(node);
            ExitBlock();
        }

        // --- Nesting depth tracking ---

        public override void VisitTryStatement(TryStatementSyntax node)
        {
            EnterBlock();
            base.VisitTryStatement(node);
            ExitBlock();
        }

        // Lambda/local functions don't increase nesting of the parent method
        // (they're separate logical units), so we don't track them

        private void EnterBlock()
        {
            _currentDepth++;
            if (_currentDepth > MaxNestingDepth)
                MaxNestingDepth = _currentDepth;
        }

        private void ExitBlock()
        {
            _currentDepth--;
        }
    }
}
