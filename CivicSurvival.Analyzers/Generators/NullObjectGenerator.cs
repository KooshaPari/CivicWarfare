using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CivicSurvival.Analyzers.Generators;

[Generator]
public class NullObjectGenerator : IIncrementalGenerator
{
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
                p.Type?.ToFullString() ?? "object",
                p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false,
                p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false))
            .ToImmutableArray();

        var methods = interfaceDecl.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(m => new MethodInfo(
                m.Identifier.ValueText,
                m.ReturnType?.ToFullString() ?? "void",
                m.ParameterList?.Parameters.Select(p => new ParameterInfo(p.Identifier.ValueText, p.Type?.ToFullString() ?? "object")).ToImmutableArray() ?? ImmutableArray<ParameterInfo>.Empty))
            .ToImmutableArray();

        // Collect `using` directives from the compilation unit that
        // contains the interface. The generated code needs the same
        // usings so interface-typed members (e.g. `Range`, `Entity`)
        // resolve at compile time.
        var sourceUsings = interfaceDecl.SyntaxTree.GetCompilationUnitRoot()
            .Usings
            .Select(u => u.Name?.ToString())
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToImmutableArray();

        return new InterfaceInfo(
            interfaceSymbol.ContainingNamespace.ToDisplayString(),
            interfaceSymbol.Name,
            interfaceSymbol.TypeParameters.Select(t => t.Name).ToImmutableArray(),
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
        var className = $"Null{info.Name}";
        var namespaceName = info.Namespace;
        var typeParams = info.TypeParameters.Length > 0 ? $"<{string.Join(", ", info.TypeParameters)}>" : "";
        var typeConstraints = info.TypeParameters.Length > 0 ? $" where {string.Join(" where ", info.TypeParameters.Select(t => $"{t} : class"))}" : "";

        var members = info.Members;
        var methods = info.Methods;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// GENERATED - DO NOT EDIT");
        sb.AppendLine($"// Source: {info.Name}");
        sb.AppendLine($"// Generator: CivicSurvival.Analyzers.NullObjectGenerator");
        sb.AppendLine($"// GeneratedAt: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Collections.Immutable;");
        sb.AppendLine("using System.Linq;");
        // Forward the source interface's using directives so generated code
        // can resolve interface-typed property/method return values that
        // live in namespaces the interface itself imports.
        if (info.SourceUsings != null)
        {
            foreach (var u in info.SourceUsings.OrderBy(x => x))
            {
                sb.AppendLine($"using {u};");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>");
        sb.AppendLine($"    /// Null object implementation for {info.Name}. Returns default/empty values for all members.");
        sb.AppendLine($"    /// </summary>");
        sb.AppendLine($"    public sealed class {className}{typeParams} : {info.Name}{typeParams}{typeConstraints}");
        sb.AppendLine("    {");

        // Generate property implementations
        foreach (var member in members)
        {
            var returnType = member.Type;
            var defaultValue = GetDefaultValue(returnType);

            if (member.HasGetter && member.HasSetter)
            {
                sb.AppendLine($"        public {returnType} {member.Name} {{ get; set; }} = {defaultValue};");
            }
            else if (member.HasGetter)
            {
                sb.AppendLine($"        public {returnType} {member.Name} => {defaultValue};");
            }
        }

        // Generate method implementations
        foreach (var method in methods)
        {
            var returnType = method.ReturnType;
            var defaultValue = GetDefaultValue(returnType);
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

    private static string GetDefaultValue(string type)
    {
        // Handle common types
        return type switch
        {
            "string" => "string.Empty",
            "bool" => "false",
            "int" or "int32" or "System.Int32" => "0",
            "long" or "int64" or "System.Int64" => "0L",
            "float" or "single" or "System.Single" => "0f",
            "double" or "System.Double" => "0.0",
            "decimal" or "System.Decimal" => "0m",
            "Guid" or "System.Guid" => "Guid.Empty",
            "DateTime" or "System.DateTime" => "default",
            "DateTimeOffset" or "System.DateTimeOffset" => "default",
            "TimeSpan" or "System.TimeSpan" => "default",
            _ when type.EndsWith("?") => "null",
            _ when type.StartsWith("IEnumerable<") || type.StartsWith("IList<") || type.StartsWith("IReadOnlyList<") || type.StartsWith("ImmutableArray<") || type.StartsWith("ImmutableList<") => "ImmutableArray<T>.Empty", // simplified
            _ when type.StartsWith("List<") || type.StartsWith("Dictionary<") || type.StartsWith("HashSet<") => "new " + type + "()",
            _ => "default"
        };
    }

    private record InterfaceInfo(
        string Namespace,
        string Name,
        ImmutableArray<string> TypeParameters,
        ImmutableArray<MemberInfo> Members,
        ImmutableArray<MethodInfo> Methods,
        ImmutableArray<string> SourceUsings);

    private record MemberInfo(string Name, string Type, bool HasGetter, bool HasSetter);
    private record MethodInfo(string Name, string ReturnType, ImmutableArray<ParameterInfo> Parameters);
    private record ParameterInfo(string Name, string Type);
}