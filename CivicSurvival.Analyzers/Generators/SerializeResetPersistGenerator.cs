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
public class SerializeResetPersistGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Bump this constant whenever the on-disk save format produced by the
    /// generated serializer changes in an incompatible way (renamed fields,
    /// removed properties, type changes, semantic re-interpretation).
    ///
    /// Every generated <c>*Serializer.g.cs</c> carries a matching
    /// <c>SaveFormatVersion</c> constant baked in at generation time. The
    /// generated Deserialize method reads the stamp from incoming data first
    /// and throws <see cref="InvalidOperationException"/> with a clear message
    /// when the version does not match — saves fail loud instead of silently
    /// corrupting into wrong-but-loadable state. Minor additive changes
    /// (new optional properties) do NOT require a bump.
    /// </summary>
    public const int CurrentSaveFormatVersion = 1;

    /// <summary>
    /// Dictionary key the generated Serialize/Deserialize pair use to carry
    /// the save-format version stamp. The leading double underscore + civic
    /// prefix keeps it out of the user-visible property namespace so it
    /// never collides with a real field. Do not rename without also writing
    /// a migration for in-flight saves.
    /// </summary>
    public const string SaveFormatVersionKey = "__civic_save_format_version";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsSerializableClass(node),
                transform: static (ctx, _) => GetClassInfo(ctx))
            .Where(static info => info != null);

        var compilation = context.CompilationProvider;

        context.RegisterSourceOutput(
            classDeclarations.Combine(compilation),
            static (ctx, source) => Execute(ctx, source.Left, source.Right));
    }

    private static bool IsSerializableClass(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax classDecl &&
               classDecl.AttributeLists.Count > 0;
    }

    private static ClassInfo? GetClassInfo(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        var classSymbol = semanticModel.GetDeclaredSymbol(classDecl);
        if (classSymbol == null)
            return null;

        var hasSerializable = classSymbol.GetAttributes()
            .Any(attr => attr.AttributeClass?.Name == "SerializableAttribute" ||
                         attr.AttributeClass?.ToDisplayString() == "System.SerializableAttribute" ||
                         attr.AttributeClass?.Name == "GenerateSerializerAttribute" ||
                         attr.AttributeClass?.ToDisplayString() == "CivicSurvival.Core.Attributes.GenerateSerializerAttribute");

        if (!hasSerializable)
            return null;

        var properties = classDecl.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(p => new PropertyInfo(
                p.Identifier.ValueText,
                p.Type?.ToString() ?? "object",
                p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false,
                p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false))
            .ToImmutableArray();

        return new ClassInfo(
            classSymbol.ContainingNamespace.ToDisplayString(),
            classSymbol.Name,
            classSymbol.TypeParameters.Select(t => t.Name).ToImmutableArray(),
            properties);
    }

    private static void Execute(SourceProductionContext context, ClassInfo? info, Compilation compilation)
    {
        if (info == null)
            return;

        var source = GenerateSerializerClass(info);
        context.AddSource($"{info.Name}Serializer.g.cs", SourceText.From(source, System.Text.Encoding.UTF8));
    }

    private static string GenerateSerializerClass(ClassInfo info)
    {
        var className = $"{info.Name}Serializer";
        var namespaceName = info.Namespace;
        var typeParams = info.TypeParameters.Length > 0 ? $"<{string.Join(", ", info.TypeParameters)}>" : "";
        var typeConstraints = info.TypeParameters.Length > 0 ? $" where {string.Join(" where ", info.TypeParameters.Select(t => $"{t} : class"))}" : "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// GENERATED - DO NOT EDIT");
        sb.AppendLine($"// Source: {info.Name}");
        sb.AppendLine($"// Generator: CivicSurvival.Analyzers.SerializeResetPersistGenerator");
        sb.AppendLine($"// GeneratedAt: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Collections.Immutable;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>");
        sb.AppendLine($"    /// Serialization helpers for {info.Name}.");
        sb.AppendLine($"    /// </summary>");
        sb.AppendLine($"    public static class {className}{typeParams}{typeConstraints}");
        sb.AppendLine("    {");

        // Save-format version stamp baked at generation time. Stamped into
        // every Serialize output and verified at the top of every Deserialize
        // so saves fail loud on version mismatch instead of silently parsing
        // wrong-but-loadable data. See SerializeResetPersistGenerator.CurrentSaveFormatVersion.
        sb.AppendLine($"        /// <summary>");
        sb.AppendLine($"        /// Save-format version this serializer emits. Bumped in lockstep with");
        sb.AppendLine($"        /// <see cref=\"CivicSurvival.Analyzers.Generators.SerializeResetPersistGenerator.CurrentSaveFormatVersion\"/>");
        sb.AppendLine($"        /// when the on-disk shape changes incompatibly. Deserialize throws on mismatch.");
        sb.AppendLine($"        /// </summary>");
        sb.AppendLine($"        public const int SaveFormatVersion = {CurrentSaveFormatVersion};");
        sb.AppendLine();

        // Serialize method — stamp the version first so any consumer that
        // peeks at the data sees the format marker before the payload.
        sb.AppendLine($"        /// <summary>Serializes the instance to a dictionary, stamped with SaveFormatVersion.</summary>");
        sb.AppendLine($"        public static Dictionary<string, object> Serialize{info.Name}{typeParams}({info.Name}{typeParams} instance)");
        sb.AppendLine("        {");
        sb.AppendLine("            var result = new Dictionary<string, object>();");
        sb.AppendLine($"            result[\"{SaveFormatVersionKey}\"] = SaveFormatVersion;");
        foreach (var prop in info.Properties)
        {
            sb.AppendLine($"            result[\"{prop.Name}\"] = instance.{prop.Name};");
        }
        sb.AppendLine("            return result;");
        sb.AppendLine("        }");

        // Deserialize method — verify the stamp first. Missing stamp or
        // version mismatch throws InvalidOperationException so the load
        // fails loud instead of producing a partially-populated instance
        // that downstream code treats as a valid save.
        sb.AppendLine($"        /// <summary>Deserializes from a dictionary. Throws on SaveFormatVersion mismatch.</summary>");
        sb.AppendLine($"        public static {info.Name}{typeParams} Deserialize{info.Name}{typeParams}(Dictionary<string, object> data)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (data == null) throw new ArgumentNullException(nameof(data));");
        sb.AppendLine($"            if (!data.TryGetValue(\"{SaveFormatVersionKey}\", out var versionValue) || versionValue == null)");
        sb.AppendLine("                throw new InvalidOperationException(");
        sb.AppendLine($"                    $\"Save data for {info.Name} is missing the {SaveFormatVersionKey} stamp — corrupted or written by an incompatible serializer. Expected version {{SaveFormatVersion}}.\");");
        sb.AppendLine("            if (versionValue is int versionInt)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (versionInt != SaveFormatVersion)");
        sb.AppendLine($"                    throw new InvalidOperationException($\"Save data for {info.Name} is version {{versionInt}} but this serializer expects {{SaveFormatVersion}}. The save cannot be loaded by this build.\");");
        sb.AppendLine("            }");
        sb.AppendLine("            else if (versionValue is long versionLong)");
        sb.AppendLine("            {");
        sb.AppendLine("                if ((int)versionLong != SaveFormatVersion)");
        sb.AppendLine($"                    throw new InvalidOperationException($\"Save data for {info.Name} is version {{versionLong}} but this serializer expects {{SaveFormatVersion}}. The save cannot be loaded by this build.\");");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine($"                throw new InvalidOperationException($\"Save data for {info.Name} carries an unreadable {SaveFormatVersionKey} stamp (type {{versionValue.GetType().Name}}). Expected version {{SaveFormatVersion}}.\");");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine($"            var instance = new {info.Name}{typeParams}();");
        foreach (var prop in info.Properties)
        {
            sb.AppendLine($"            if (data.TryGetValue(\"{prop.Name}\", out var value) && value != null)");
            sb.AppendLine($"                instance.{prop.Name} = ({prop.Type})value;");
        }
        sb.AppendLine("            return instance;");
        sb.AppendLine("        }");

        // ResetPersistFields method
        sb.AppendLine($"        /// <summary>Resets all fields to default values.</summary>");
        sb.AppendLine($"        public static void ResetPersistFields{info.Name}{typeParams}({info.Name}{typeParams} instance)");
        sb.AppendLine("        {");
        foreach (var prop in info.Properties)
        {
            var defaultValue = GetDefaultValue(prop.Type);
            sb.AppendLine($"            instance.{prop.Name} = {defaultValue};");
        }
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GetDefaultValue(string type)
    {
        return type switch
        {
            "string" => "string.Empty",
            "bool" => "false",
            "int" or "int32" or "System.Int32" => "0",
            "long" or "int64" or "System.Int64" => "0L",
            "float" or "single" or "System.Single" => "0f",
            "double" or "System.Double" => "0.0",
            "decimal" or "System.Decimal" => "0m",
            _ => "default"
        };
    }

    private record ClassInfo(
        string Namespace,
        string Name,
        ImmutableArray<string> TypeParameters,
        ImmutableArray<PropertyInfo> Properties);

    private record PropertyInfo(string Name, string Type, bool HasGetter, bool HasSetter);
}