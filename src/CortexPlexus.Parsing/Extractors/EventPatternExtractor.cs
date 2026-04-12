using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Extractors;

/// <summary>
/// Detects advanced event/messaging patterns in C# code:
/// - MediatR: IMediator.Send(new XCommand()), IMediator.Publish(new XNotification())
/// - Domain events: DomainEvents.Raise(new X()), EventBus.Publish(new X())
/// - Delegate invocation: Action/Func invoke patterns
/// Creates Publishes edges from the calling method to the event/command type.
/// </summary>
internal sealed class EventPatternExtractor : CSharpSyntaxWalker
{
    private static readonly HashSet<string> PublishMethods = new(StringComparer.Ordinal)
    {
        "Send", "Publish", "Raise", "Dispatch", "Emit", "Notify", "Fire",
    };

    private static readonly HashSet<string> MediatorTypes = new(StringComparer.Ordinal)
    {
        "MediatR.IMediator",
        "MediatR.ISender",
        "MediatR.IPublisher",
    };

    private readonly SemanticModel _semanticModel;
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _seen = [];

    public IReadOnlyList<Relationship> Relationships => _relationships;

    public EventPatternExtractor(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;

            // MediatR: _mediator.Send(new CreateOrderCommand())
            // MediatR: _mediator.Publish(new OrderCreatedNotification())
            if (PublishMethods.Contains(methodName))
            {
                var callerFqn = GetEnclosingMethodFqn(node);
                if (callerFqn is null)
                {
                    base.VisitInvocationExpression(node);
                    return;
                }

                // Check if receiver is MediatR type
                var receiverType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
                var isMediatR = false;
                if (receiverType is not null)
                {
                    if (MediatorTypes.Contains(receiverType.ToDisplayString()))
                        isMediatR = true;
                    else if (receiverType is INamedTypeSymbol namedType)
                        isMediatR = namedType.AllInterfaces.Any(i => MediatorTypes.Contains(i.ToDisplayString()));
                }

                if (isMediatR && node.ArgumentList.Arguments.Count >= 1)
                {
                    // Extract the type of the first argument (the command/event)
                    var argType = _semanticModel.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                    if (argType is not null)
                    {
                        var eventTypeFqn = argType.ToDisplayString();
                        var relType = methodName == "Send" ? RelationshipType.Calls : RelationshipType.Publishes;
                        AddEdge(callerFqn, eventTypeFqn, relType, "MediatR");
                    }
                }
                // Generic domain event patterns: eventBus.Publish(new XEvent()), domainEvents.Raise(new X())
                else if (!isMediatR && node.ArgumentList.Arguments.Count >= 1)
                {
                    var firstArg = node.ArgumentList.Arguments[0].Expression;
                    if (firstArg is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
                    {
                        var argType = _semanticModel.GetTypeInfo(firstArg).Type;
                        if (argType is not null)
                        {
                            var eventTypeFqn = argType.ToDisplayString();
                            AddEdge(callerFqn, eventTypeFqn, RelationshipType.Publishes, "DomainEvent");
                        }
                    }
                }
            }
        }

        // Delegate/Action/Func invocation: callback(args) or callback.Invoke(args)
        if (node.Expression is IdentifierNameSyntax identifier)
        {
            var symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
            if (symbol is IParameterSymbol param && IsDelegateType(param.Type))
            {
                var callerFqn = GetEnclosingMethodFqn(node);
                if (callerFqn is not null)
                {
                    var delegateFqn = param.Type.ToDisplayString();
                    AddEdge(callerFqn, delegateFqn, RelationshipType.Publishes, "Delegate");
                }
            }
        }

        base.VisitInvocationExpression(node);
    }

    // --- INotificationHandler<T> → Subscribes edge ---
    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol is INamedTypeSymbol classSymbol)
        {
            foreach (var iface in classSymbol.AllInterfaces)
            {
                var ifaceFqn = iface.OriginalDefinition.ToDisplayString();

                // MediatR IRequestHandler<TRequest, TResponse> or INotificationHandler<TNotification>
                if (ifaceFqn.StartsWith("MediatR.IRequestHandler") && iface.TypeArguments.Length >= 1)
                {
                    var requestType = iface.TypeArguments[0].ToDisplayString();
                    AddEdge(requestType, SymbolExtractor.GetFqn(classSymbol), RelationshipType.HandledBy, "MediatR");
                }
                else if (ifaceFqn.StartsWith("MediatR.INotificationHandler") && iface.TypeArguments.Length >= 1)
                {
                    var notificationType = iface.TypeArguments[0].ToDisplayString();
                    AddEdge(notificationType, SymbolExtractor.GetFqn(classSymbol), RelationshipType.HandledBy, "MediatR");
                }
            }
        }

        base.VisitClassDeclaration(node);
    }

    private static bool IsDelegateType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Delegate) return true;
        var fqn = type.ToDisplayString();
        return fqn.StartsWith("System.Action") || fqn.StartsWith("System.Func");
    }

    private void AddEdge(string fromFqn, string toFqn, RelationshipType type, string provider)
    {
        if (string.IsNullOrWhiteSpace(fromFqn) || string.IsNullOrWhiteSpace(toFqn))
            return;

        var key = $"{fromFqn}->{toFqn}:{type}";
        if (!_seen.Add(key)) return;

        _relationships.Add(new Relationship(fromFqn, toFqn, type,
            new Dictionary<string, string> { ["provider"] = provider }));
    }

    private string? GetEnclosingMethodFqn(SyntaxNode node)
    {
        var enclosing = node.Ancestors()
            .FirstOrDefault(a => a is MethodDeclarationSyntax or ConstructorDeclarationSyntax);
        if (enclosing is null) return null;
        var symbol = _semanticModel.GetDeclaredSymbol(enclosing) as IMethodSymbol;
        return symbol is not null ? SymbolExtractor.GetFqn(symbol) : null;
    }
}
