using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CivicSurvival.Analyzers.Generators;

/// <summary>Emits immutable null objects using each interface's declared fallback values.</summary>
[Generator]
public class NullObjectGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var nullObjectInterfaces = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateInterface(node),
                transform: static (ctx, _) => GetInterfaceInfo(ctx))
            .Where(static info => info != null);

        var compilation = context.CompilationProvider;

        context.RegisterSourceOutput(
            nullObjectInterfaces.Combine(compilation),
            static (ctx, source) => Execute(ctx, source.Left, source.Right));
    }

    private static bool IsCandidateInterface(SyntaxNode node)
    {
        return node is InterfaceDeclarationSyntax interfaceDecl &&
               interfaceDecl.AttributeLists.Count > 0;
    }

    private static InterfaceInfo? GetInterfaceInfo(GeneratorSyntaxContext context)
    {
        var interfaceDecl = (InterfaceDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        var interfaceSymbol = semanticModel.GetDeclaredSymbol(interfaceDecl);
        if (interfaceSymbol == null)
            return null;

        var hasGenerateNullObject = interfaceSymbol.GetAttributes()
            .Any(attr => attr.AttributeClass?.Name == "GenerateNullObjectAttribute" ||
                         attr.AttributeClass?.ToDisplayString() == "CivicSurvival.Core.Attributes.GenerateNullObjectAttribute");

        if (!hasGenerateNullObject)
            return null;

        var members = interfaceDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(p => new MemberInfo(
                p.Identifier.ValueText,
                p.Type.ToString().Trim(),
                p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false,
                p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false,
                GetReturnValue(p.Type, p.AttributeLists)))
            .ToImmutableArray();

        var methods = interfaceDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(m => new MethodInfo(
                m.Identifier.ValueText,
                m.ReturnType.ToString().Trim(),
                m.ParameterList.Parameters.Select(p => new ParameterInfo(p.Identifier.Text, p.Type?.ToString().Trim() ?? "object")).ToImmutableArray(),
                GetReturnValue(m.ReturnType, m.AttributeLists)))
            .ToImmutableArray();

        // Collect `using` directives from the compilation unit that
        // contains the interface. The generated code needs the same
        // usings so interface-typed members (e.g. `Range`, `Entity`)
        // resolve at compile time.
        var sourceUsings = interfaceDecl.SyntaxTree.GetCompilationUnitRoot()
            .Usings
            .Select(u => u.WithoutTrivia().ToString())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToImmutableArray();

        return new InterfaceInfo(
            interfaceSymbol.ContainingNamespace.ToDisplayString(),
            interfaceSymbol.Name,
            interfaceSymbol.TypeParameters.Select(t => t.Name).ToImmutableArray(),
            string.Join(" ", interfaceDecl.ConstraintClauses.Select(c => c.ToString())),
            members,
            methods,
            sourceUsings);
    }

    private static void Execute(SourceProductionContext context, InterfaceInfo? info, Compilation compilation)
    {
        if (info == null)
            return;

        var source = GenerateNullObjectClass(info);
        context.AddSource($"{info.Name}.NullObject.g.cs", SourceText.From(source, System.Text.Encoding.UTF8));
    }

    private static string GenerateNullObjectClass(InterfaceInfo info)
    {
        var className = $"Null{(info.Name.StartsWith("I", StringComparison.Ordinal) ? info.Name.Substring(1) : info.Name)}";
        var namespaceName = info.Namespace;
        var typeParams = info.TypeParameters.Length > 0 ? $"<{string.Join(", ", info.TypeParameters)}>" : "";
        var typeConstraints = info.TypeConstraints.Length > 0 ? " " + info.TypeConstraints : "";

        var members = info.Members;
        var methods = info.Methods;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// GENERATED - DO NOT EDIT");
        sb.AppendLine($"// Source: {info.Name}");
        sb.AppendLine($"// Generator: CivicSurvival.Analyzers.NullObjectGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        // Forward the source interface's using directives so generated code
        // can resolve interface-typed property/method return values that
        // live in namespaces the interface itself imports.
        if (info.SourceUsings != null)
        {
            foreach (var u in info.SourceUsings.OrderBy(x => x))
            {
                sb.AppendLine(u);
            }
        }
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>");
        sb.AppendLine($"    /// Null object implementation for {info.Name}. Returns default/empty values for all members.");
        sb.AppendLine($"    /// </summary>");
        sb.AppendLine($"    internal sealed class {className}{typeParams} : {info.Name}{typeParams}{typeConstraints}");
        sb.AppendLine("    {");
        // Generic service lookup must infer the owned interface, not the implementation
        // class (which has neither the registry key nor OwnedByFeatureId metadata).
        sb.AppendLine($"        public static readonly {info.Name}{typeParams} Instance = new {className}{typeParams}();");
        sb.AppendLine($"        private {className}() {{ }}");

        // Generate property implementations
        foreach (var member in members)
        {
            var returnType = member.Type;
            var defaultValue = member.DefaultValue;

            if (member.HasGetter && member.HasSetter)
            {
                sb.AppendLine($"        public {returnType} {member.Name} {{ get => {defaultValue}; set {{ }} }}");
            }
            else if (member.HasGetter)
            {
                sb.AppendLine($"        public {returnType} {member.Name} => {defaultValue};");
            }
            else if (member.HasSetter)
            {
                sb.AppendLine($"        public {returnType} {member.Name} {{ set {{ }} }}");
            }
        }

        // Generate method implementations
        foreach (var method in methods)
        {
            var returnType = method.ReturnType;
            var defaultValue = method.DefaultValue;
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));

            sb.AppendLine($"        public {returnType} {method.Name}({parameters})");
            sb.AppendLine("        {");
            if (returnType != "void")
            {
                sb.AppendLine($"            return {defaultValue};");
            }
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GetReturnValue(TypeSyntax returnType, SyntaxList<AttributeListSyntax> lists)
    {
        var type = returnType.ToString().Trim();
        foreach (var attribute in lists.SelectMany(list => list.Attributes))
        {
            var name = attribute.Name.ToString().Split('.').Last();
            if (name.EndsWith("Attribute", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - "Attribute".Length);
            if (name == "NullReturn" && attribute.ArgumentList?.Arguments.Count == 1)
                return attribute.ArgumentList.Arguments[0].Expression.ToString();
            if (name == "NullReturnNull")
                return "null";
            if (name == "NullReturnEmpty")
            {
                if (returnType is ArrayTypeSyntax array)
                    return $"global::System.Array.Empty<{array.ElementType}>()";
                var generic = returnType.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault();
                if (generic != null && generic.TypeArgumentList.Arguments.Count == 1)
                {
                    var element = generic.TypeArgumentList.Arguments[0];
                    if (generic.Identifier.ValueText == "ImmutableArray" || generic.Identifier.ValueText == "ImmutableList")
                        return $"{type}.Empty";
                    if (generic.Identifier.ValueText.StartsWith("I", StringComparison.Ordinal))
                        return $"global::System.Array.Empty<{element}>()";
                }
                return $"new {type}()";
            }
            if (name == "NullReturnCompletedDefault")
            {
                var generic = returnType.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault();
                return generic == null ? "global::System.Threading.Tasks.Task.CompletedTask"
                    : $"global::System.Threading.Tasks.Task.FromResult(default({generic.TypeArgumentList.Arguments[0]}))";
            }
        }
        return "default";
    }

    private record InterfaceInfo(
        string Namespace,
        string Name,
        ImmutableArray<string> TypeParameters,
        string TypeConstraints,
        ImmutableArray<MemberInfo> Members,
        ImmutableArray<MethodInfo> Methods,
        ImmutableArray<string> SourceUsings);

    private record MemberInfo(string Name, string Type, bool HasGetter, bool HasSetter, string DefaultValue);
    private record MethodInfo(string Name, string ReturnType, ImmutableArray<ParameterInfo> Parameters, string DefaultValue);
    private record ParameterInfo(string Name, string Type);
}
