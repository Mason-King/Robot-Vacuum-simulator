using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The source half of the plugin sandbox (<see cref="AlgorithmSandbox"/>, requirement 4.8): after a plugin
    /// compiles, and before it is loaded, every type and member its code names or produces is checked against the
    /// sandbox's list. Types written in the plugin itself and the algorithm API are always allowed; everything else
    /// must be on the list, including generic arguments, array elements, base types, attributes and the types of
    /// expressions (so `x.GetType()` or `typeof(T)` is refused even without naming System.Type). A few constructs
    /// are refused outright: extern methods, lock, finalizers, and the undocumented __makeref family.
    ///
    /// Only called from <see cref="AlgorithmCompiler"/>, inside the method that needs Roslyn, so the simulator still
    /// loads when Roslyn is missing.
    /// </summary>
    static class AlgorithmSourceSandbox
    {
        public static void Check(CSharpCompilation compilation, List<string> errors)
        {
            var check = new Checker(compilation, errors);
            foreach (var tree in compilation.SyntaxTrees) check.Tree(tree);
        }

        sealed class Checker
        {
            readonly CSharpCompilation compilation;
            readonly List<string> errors;
            readonly HashSet<string> reported = new HashSet<string>();
            readonly string api = AlgorithmSandbox.ApiAssemblyName;
            Location at;

            public Checker(CSharpCompilation compilation, List<string> errors)
            {
                this.compilation = compilation;
                this.errors = errors;
            }

            public void Tree(SyntaxTree tree)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var token in root.DescendantTokens())
                {
                    if (!token.IsKind(SyntaxKind.ExternKeyword)) continue;
                    at = token.GetLocation();
                    Report("extern", $"{AlgorithmSandbox.Code} 'extern' (native code) is not available to algorithms.");
                }

                foreach (var node in root.DescendantNodes())
                {
                    at = node.GetLocation();
                    if (node is LockStatementSyntax)
                    {
                        Report("lock", $"{AlgorithmSandbox.Code} 'lock' is not available to algorithms: an algorithm runs on the simulation's thread only.");
                        continue;
                    }
                    if (node is DestructorDeclarationSyntax)
                    {
                        Report("finalizer", $"{AlgorithmSandbox.Code} Finalizers (~ClassName) are not available to algorithms: they run on another thread.");
                        continue;
                    }
                    if (node.IsKind(SyntaxKind.MakeRefExpression) || node.IsKind(SyntaxKind.RefTypeExpression) ||
                        node.IsKind(SyntaxKind.RefValueExpression) || node.IsKind(SyntaxKind.ArgListExpression))
                    {
                        Report("makeref", $"{AlgorithmSandbox.Code} __makeref, __reftype, __refvalue and __arglist are not available to algorithms.");
                        continue;
                    }

                    if (!(node is ExpressionSyntax || node is ConstructorInitializerSyntax || node is AttributeSyntax)) continue;

                    var info = model.GetSymbolInfo(node);
                    Symbol(info.Symbol);
                    foreach (var candidate in info.CandidateSymbols) Symbol(candidate);

                    if (node is ExpressionSyntax expression)
                    {
                        var type = model.GetTypeInfo(expression);
                        Type(type.Type);
                        Type(type.ConvertedType);
                    }
                }
            }

            void Symbol(ISymbol symbol)
            {
                switch (symbol)
                {
                    case null:
                        return;
                    case IAliasSymbol alias:
                        Symbol(alias.Target);
                        return;
                    case ITypeSymbol type:
                        Type(type);
                        return;
                    case IMethodSymbol method:
                        var definition = method.ReducedFrom ?? method;
                        Owner(definition.ContainingType, definition.Name);
                        Type(method.ReturnType);
                        foreach (var parameter in method.Parameters) Type(parameter.Type);
                        foreach (var argument in method.TypeArguments) Type(argument);
                        return;
                    case IPropertySymbol property:
                        Owner(property.ContainingType, property.Name);
                        Type(property.Type);
                        foreach (var parameter in property.Parameters) Type(parameter.Type);
                        return;
                    case IFieldSymbol field:
                        Owner(field.ContainingType, field.Name);
                        Type(field.Type);
                        return;
                    case IEventSymbol @event:
                        Owner(@event.ContainingType, @event.Name);
                        Type(@event.Type);
                        return;
                    default:
                        return; // namespaces, locals, parameters, labels, range variables: their types are checked where used
                }
            }

            void Owner(INamedTypeSymbol type, string member)
            {
                if (type == null) return;
                Type(type);
                if (Trusted(type)) return;
                string name = FullName(type);
                if (!AlgorithmSandbox.IsMemberAllowed(name, member))
                    Report(name + "." + member, AlgorithmSandbox.Refusal(type.ToDisplayString() + "." + member, name + "." + member));
            }

            void Type(ITypeSymbol type, int depth = 0)
            {
                if (type == null || depth > 32 || type.TypeKind == TypeKind.Error) return;
                switch (type)
                {
                    case ITypeParameterSymbol _:
                        return;
                    case IArrayTypeSymbol array:
                        Type(array.ElementType, depth + 1);
                        return;
                    case IPointerTypeSymbol _:
                    case IFunctionPointerTypeSymbol _:
                        Report("pointer", $"{AlgorithmSandbox.Code} Pointers are not available to algorithms.");
                        return;
                }
                if (type.TypeKind == TypeKind.Dynamic)
                {
                    Report("dynamic", $"{AlgorithmSandbox.Code} 'dynamic' is not available to algorithms: it works by reflection.");
                    return;
                }
                if (!(type is INamedTypeSymbol named)) return;

                for (var level = named; level != null; level = level.ContainingType)
                    foreach (var argument in level.TypeArguments) Type(argument, depth + 1);
                if (named.IsAnonymousType || Trusted(named)) return;

                string name = FullName(named);
                if (!AlgorithmSandbox.IsTypeAllowed(name))
                    Report(name, AlgorithmSandbox.Refusal(Outermost(named).ToDisplayString(), name));
            }

            /// <summary>Written in the plugin itself, or part of the algorithm API.</summary>
            bool Trusted(INamedTypeSymbol type)
            {
                var assembly = Outermost(type).ContainingAssembly;
                if (assembly == null) return false;
                return SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly) || assembly.Name == api;
            }

            static INamedTypeSymbol Outermost(INamedTypeSymbol type)
            {
                var outer = type.OriginalDefinition;
                while (outer.ContainingType != null) outer = outer.ContainingType;
                return outer;
            }

            /// <summary>Metadata full name of the outermost type, e.g. System.Collections.Generic.List`1.</summary>
            static string FullName(INamedTypeSymbol type)
            {
                var outer = Outermost(type);
                var ns = outer.ContainingNamespace;
                string space = ns == null || ns.IsGlobalNamespace ? "" : ns.ToDisplayString();
                return space.Length == 0 ? outer.MetadataName : space + "." + outer.MetadataName;
            }

            void Report(string key, string message)
            {
                var span = at.GetLineSpan();
                int line = span.StartLinePosition.Line + 1;
                if (!reported.Add($"{span.Path}|{line}|{key}")) return;
                string file = string.IsNullOrEmpty(span.Path) ? "source" : span.Path;
                errors.Add($"{file}({line},{span.StartLinePosition.Character + 1}): {message}");
            }
        }
    }
}
