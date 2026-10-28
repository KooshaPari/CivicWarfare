using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using CivicSurvival.Analyzers.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class NullObjectGeneratorDriver
{
    public static void Main(string[] args)
    {
        var root = args[0];
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var files = new[] { "GenerateNullObjectAttribute", "NullReturnAttribute", "NullReturnEmptyAttribute",
            "NullReturnNullAttribute", "NullReturnCompletedDefaultAttribute" }
            .Select(name => "CivicSurvival/Core/Attributes/" + name + ".cs")
            .Append("tests/fixtures/null_object_generator_input.cs");
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), options, file));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("NullFixtures", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new NullObjectGenerator().AsSourceGenerator() }, parseOptions: options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new Exception(string.Join("\n", diagnostics));
        var generated = driver.GetRunResult().Results.Single().GeneratedSources;
        if (generated.Length != 3) throw new Exception("Expected all three null implementations");
        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        if (!emitted.Success) throw new Exception(string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        AssemblyLoadContext.Default.LoadFromStream(stream).GetType("NullFixtures.Checks")!
            .GetMethod("Run")!.Invoke(null, null);
        Console.WriteLine("null generator emitted-source behavior passed");
    }
}
