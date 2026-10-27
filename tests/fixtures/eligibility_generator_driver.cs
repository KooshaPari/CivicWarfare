using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using CivicSurvival.Analyzers.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class EligibilityDriver
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    public static void Main(string[] args)
    {
        var root = args[0];
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var files = new[] {
            "CivicSurvival/Core/UI/DomainState/Attributes/DtoEligibilityAttribute.cs",
            "CivicSurvival/Core/UI/DomainState/DomainJsonHelper.cs",
            "tests/fixtures/eligibility_generator_input.cs"
        };
        var trees = files.Select(path => CSharpSyntaxTree.ParseText(
            File.ReadAllText(Path.Combine(root, path)), parseOptions, path)).ToList();
        // Include the actual writer argument type without unrelated game-bound ActionGate code.
        var actionGate = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,
            "CivicSurvival/Core/UI/DomainState/ActionGate.cs")), parseOptions);
        var availability = actionGate.GetRoot().DescendantNodes().OfType<StructDeclarationSyntax>()
            .Single(node => node.Identifier.ValueText == "ActionAvailabilityField");
        trees.Add(CSharpSyntaxTree.ParseText("namespace CivicSurvival.Core.UI.DomainState {\n"
            + availability.ToFullString() + "\n}", parseOptions));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("EligibilityOutput", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new LockedReasonIdGenerator().AsSourceGenerator() }, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Check(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", diagnostics));
        var generated = driver.GetRunResult().Results.Single().GeneratedSources;
        Check(generated.Length == 2, "Expected one generated file per eligible DTO, despite partial declarations");
        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        Check(emitted.Success, string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var run = assembly.GetType("EligibilityFixtures.Projection")!.GetMethod("Run")!;
        var text = (string)run.Invoke(null, null)!;
        using var document = JsonDocument.Parse(text);
        var json = document.RootElement;
        Check(json.EnumerateObject().Count() == 9, "Unexpected/missing/duplicate serialized fields: " + text);
        Check(json.GetProperty("before").GetBoolean(), "Existing JSON field lost");
        Check(json.GetProperty("CanStart").GetBoolean(), "CanStart was not serialized as bool");
        Check(!json.GetProperty("CanResume").GetBoolean(), "CanResume was not serialized as bool");
        Check(json.GetProperty("LockedReason").GetString() == "locked\"reason", "Shared generated reason or escaping wrong");
        Check(json.GetProperty("CanStop").GetBoolean(), "Existing reason eligibility missing");
        Check(json.GetProperty("ExistingReason").GetString() == "existing", "Existing reason was overwritten");
        Check(!json.GetProperty("CanProceed").GetBoolean(), "No-reason eligibility missing");
        Check(json.GetProperty("event").GetBoolean(), "Keyword field was not escaped");
        Check(json.GetProperty("KeywordReason").GetString() == "", "Null reason must serialize empty string");
        Check(!json.TryGetProperty("Ignored", out _), "Nonboolean eligibility field was unexpectedly serialized");
        var noReason = assembly.GetType("EligibilityFixtures.NoReason")!.GetMethod("Run")!;
        using var onlyFlag = JsonDocument.Parse((string)noReason.Invoke(null, null)!);
        Check(onlyFlag.RootElement.EnumerateObject().Count() == 1
            && onlyFlag.RootElement.GetProperty("Enabled").GetBoolean(), "No-reason DTO is wrong");
        Console.WriteLine("eligibility generation and JSON checks passed");
    }
}
