using Microsoft.CodeAnalysis;

namespace CustomGenerator;

[Generator(LanguageNames.VisualBasic)]
public sealed class VbDiagnosticEmittingGenerator : IIncrementalGenerator
{
    public const string DiagnosticId = "VBDIAG001";
    public const string DiagnosticMessage = "CustomGenerator.VbDiagnosticEmittingGenerator test diagnostic";

#pragma warning disable RS2008
    private static readonly DiagnosticDescriptor Descriptor = new(
        id: DiagnosticId,
        title: "VB diagnostic emitting generator",
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
