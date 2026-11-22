using DotNetMcp.Core;
using FSharp.Compiler.Symbols;

namespace DotNetMcp.FSharp;

public sealed record FSharpHiddenDeclaration(string Name, string Signature, SymbolLocation Location);

public sealed record FSharpSignatureStatus(
    string ProjectId, long Epoch, string ImplementationFile, string? SignatureFile, string Status,
    IReadOnlyList<FSharpHiddenDeclaration> HiddenBySignature, IReadOnlyList<DiagnosticItem> CompilerErrors);

public sealed partial class FSharpSymbolQueryService
{
    public async Task<(FSharpSignatureStatus? Success, SymbolQueryError? Error)> SignatureStatusAsync(
        FSharpWorkspaceSnapshot snapshot, string projectId, string implementationFile, CancellationToken cancellationToken)
    {
        var project = snapshot.FindProject(projectId);
        if (project is null)
        {
            return (null, new ProjectNotFoundError("F# project was not found.", "Use a fsharp projectId from workspace_list_projects."));
        }

        var impl = project.Documents.FirstOrDefault(d =>
            Path.GetExtension(d.Path).Equals(".fs", StringComparison.OrdinalIgnoreCase) && SameDocumentPath(d.Path, implementationFile));
        if (impl is null)
        {
            return (null, new DocumentNotInWorkspaceError("Implementation file is not in this F# project.",
                "Pass the path of a .fs compile input in the selected project."));
        }
        var signature = project.Documents.FirstOrDefault(d => SameDocumentPath(d.Path, Path.ChangeExtension(impl.Path, ".fsi")));
        if (signature is null)
        {
            return (new(projectId, snapshot.Epoch, impl.Path, null, "noSignature", [], []), null);
        }

        var (_, check, sources) = await CheckProjectAsync(snapshot, project, snapshot.Epoch, cancellationToken).ConfigureAwait(false);
        if (check is null)
        {
            return (null, new CompilationUnavailableError("FCS could not check this project.", "Inspect project_diagnostics."));
        }
        var errors = check.Diagnostics.Where(d => d.Severity.ToString() == "Error")
            .Select(d => new DiagnosticItem($"FS{d.ErrorNumber:D4}", "Error", d.Message, d.FileName, d.StartLine,
                d.StartColumn + 1, d.EndLine, d.EndColumn + 1, projectId)).ToArray();
        if (errors.Length > 0)
        {
            // An invalid contract can cause FCS to return only part of the surface.
            var pairErrors = errors.Where(d => SameDocumentPath(d.FilePath, impl.Path) ||
                SameDocumentPath(d.FilePath, signature.Path)).ToArray();
            return pairErrors.Length > 0
                ? (new(projectId, snapshot.Epoch, impl.Path, signature.Path, "checkErrors", [], pairErrors), null)
                : (new(projectId, snapshot.Epoch, impl.Path, signature.Path, "projectCheckErrors", [], errors), null);
        }

        var definitions = check.GetAllUsesOfAllSymbols(cancellationToken).Where(use => use.IsFromDefinition).ToArray();
        var publicKeys = definitions.Where(use => SameDocumentPath(use.FileName, signature.Path))
            .Select(use => SurfaceKey(use.Symbol)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var hidden = new List<FSharpHiddenDeclaration>();
        foreach (var use in definitions.Where(use => SameDocumentPath(use.FileName, impl.Path)))
        {
            var symbol = use.Symbol;
            var key = SurfaceKey(symbol);
            if (string.IsNullOrEmpty(key) || !symbol.Accessibility.IsPublic || publicKeys.Contains(key)
                || (symbol.SignatureLocation is { } location && SameDocumentPath(location.Value.FileName, signature.Path)))
            {
                continue;
            }
            hidden.Add(new(symbol.DisplayName, key, LocationsOf(use.Range, sources)[0]));
        }
        return (new(projectId, snapshot.Epoch, impl.Path, signature.Path, "conforms",
            hidden.DistinctBy(d => d.Signature).OrderBy(d => d.Location.Start).ToArray(), []), null);
    }

    private static string? SurfaceKey(FSharpSymbol symbol) => symbol switch
    {
        FSharpMemberOrFunctionOrValue
        {
            IsModuleValueOrMember: true, IsCompilerGenerated: false,
            IsPropertyGetterMethod: false, IsPropertySetterMethod: false
        } member => member.XmlDocSig,
        FSharpEntity { IsNamespace: false } entity => entity.XmlDocSig,
        FSharpUnionCase unionCase => unionCase.XmlDocSig,
        FSharpField { IsCompilerGenerated: false, IsNameGenerated: false } field => field.XmlDocSig,
        _ => null
    };
}
