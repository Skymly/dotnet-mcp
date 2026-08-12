using Microsoft.CodeAnalysis;

namespace CustomGenerator;

/// <summary>
/// Always reports a stable diagnostic so generator-diagnostics tools can be seam-tested.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DiagnosticEmittingGenerator : IIncrementalGenerator
{
    public const string DiagnosticId = "CGDIAG001";
    public const string DiagnosticMessage = "CustomGenerator.DiagnosticEmittingGenerator test diagnostic";

#pragma warning disable RS2008 // Test fixture — no analyzer release tracking
    private static readonly DiagnosticDescriptor Descriptor = new(
        id: DiagnosticId,
        title: "Diagnostic emitting generator",
        messageFormat: DiagnosticMessage,
        category: "CustomGenerator.Tests",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
#pragma warning restore RS2008

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(
            context.CompilationProvider,
            static (productionContext, _) =>
            {
                productionContext.ReportDiagnostic(Diagnostic.Create(Descriptor, Location.None));
            });
    }
}
