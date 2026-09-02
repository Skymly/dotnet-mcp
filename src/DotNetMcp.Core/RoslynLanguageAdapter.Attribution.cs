using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    public async Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var (attribution, attrError) = await AttributeSymbolAsync(
                session, project!, symbol!, cancellationToken)
            .ConfigureAwait(false);
        if (attrError is not null)
        {
            return (null, attrError);
        }

        var members = new Dictionary<string, SymbolAttribution>(StringComparer.Ordinal);
        if (symbol is INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers()
                         .Where(m => !m.IsImplicitlyDeclared)
                         .Where(static m => m is not IMethodSymbol
                         {
                             MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd
                             or MethodKind.EventRemove or MethodKind.EventRaise
                         })
                         .OrderBy(SymbolKey, StringComparer.Ordinal))
            {
                var (memberAttr, memberError) = await AttributeSymbolAsync(
                        session, project!, member, cancellationToken)
                    .ConfigureAwait(false);
                if (memberError is not null)
                {
                    return (null, memberError);
                }

                members[SymbolKey(member)] = memberAttr!;
            }
        }

        return (new SymbolAttributionSuccess(attribution!, members), null);
    }

    private async Task<(SymbolAttribution? Attribution, SymbolQueryError? Error)> AttributeSymbolAsync(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        if (symbol.Locations.Length == 0)
        {
            return (new SymbolAttribution(DeclarationAvailability.None, SymbolOrigin.Handwritten, null), null);
        }

        if (symbol.Locations.All(l => l.IsInMetadata))
        {
            return (new SymbolAttribution(DeclarationAvailability.InMetadata, SymbolOrigin.Handwritten, null), null);
        }

        var declaring = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        SyntaxTree? tree;
        if (declaring is null)
        {
            var anySource = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (anySource is null)
            {
                return (new SymbolAttribution(DeclarationAvailability.None, SymbolOrigin.Handwritten, null), null);
            }

            tree = anySource.SourceTree;
        }
        else
        {
            tree = declaring.SyntaxTree;
        }

        var (originLabel, originError) = await ResolveOriginAsync(
                session,
                project,
                tree,
                cancellationToken)
            .ConfigureAwait(false);
        if (originError is not null)
        {
            return (null, originError);
        }

        return (ToAttribution(DeclarationAvailability.InSource, originLabel), null);
    }

    private static SymbolAttribution ToAttribution(string availability, string? originLabel)
    {
        if (originLabel is not null &&
            originLabel.StartsWith(SymbolOrigin.SourceGenerator + "(", StringComparison.Ordinal) &&
            TryParseSourceGeneratorOrigin(originLabel, out var identity))
        {
            return new SymbolAttribution(availability, SymbolOrigin.SourceGenerator, identity);
        }

        return new SymbolAttribution(availability, SymbolOrigin.Handwritten, null);
    }

    private static bool TryParseSourceGeneratorOrigin(string origin, out GeneratorIdentity identity)
    {
        identity = new GeneratorIdentity(string.Empty, string.Empty, string.Empty);
        var prefix = SymbolOrigin.SourceGenerator + "(";
        if (!origin.StartsWith(prefix, StringComparison.Ordinal) || !origin.EndsWith(')'))
        {
            return false;
        }

        var inner = origin[prefix.Length..^1];
        var at = inner.LastIndexOf('@');
        var sep = inner.IndexOf("::", StringComparison.Ordinal);
        if (at <= 0 || sep <= 0 || at <= sep + 2)
        {
            return false;
        }

        identity = new GeneratorIdentity(
            inner[..sep],
            inner[(sep + 2)..at],
            inner[(at + 1)..]);
        return true;
    }

}
