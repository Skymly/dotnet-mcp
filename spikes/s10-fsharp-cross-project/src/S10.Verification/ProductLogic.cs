// [COPIED] logic lifted from src/DotNetMcp.FSharp/*.cs so the spike exercises the exact
// matching/rewrite rules the product uses today:
//   - EntityFullName / MemberFullName / FormatParameterSignature / FormatFSharpType
//     (FSharpSymbolQueryService.cs)
//   - SameSymbol / MatchCatalog-style comparison (FSharpSymbolQueryService.Analysis.cs),
//     with FSharpCatalogItem replaced by the spike's (string Signature, string Display)
//   - ToSpan / OffsetOf, IsSimpleIdentifierUse, LastIdentifier, ApplyReplacements
//     (FSharpSymbolQueryService.Rename.cs / FSharpSymbolQueryService.cs)

using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using FcsRange = FSharp.Compiler.Text.Range;

namespace S10.Verification;

/// <summary>Spike stand-in for the product's FSharpCatalogItem.</summary>
public sealed record CatalogItem(
    string SignatureQualifiedName,
    string DisplayName,
    bool IsContainer,
    IReadOnlyList<string> LocationPaths);

public static class ProductLogic
{
    // ---------- compiler args (copied from FSharpSymbolQueryService.cs) ----------

    public static string[] BuildCompilerArgs(
        string dllName,
        IEnumerable<string> sourceFiles,
        IEnumerable<string>? defines = null,
        IEnumerable<string>? extraReferences = null)
    {
        var args = new List<string>
        {
            "--simpleresolution",
            "--targetprofile:netcore",
            "--target:library",
            "--nowin32manifest",
            "--nocopyfsharpcore",
            "--out:" + dllName,
        };

        if (defines is not null)
        {
            foreach (var define in defines)
            {
                if (!string.IsNullOrWhiteSpace(define))
                {
                    args.Add("--define:" + define);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in CompilerReferences().Concat(extraReferences ?? []))
        {
            if (string.IsNullOrWhiteSpace(reference) || !seen.Add(reference))
            {
                continue;
            }

            args.Add("-r:" + reference);
        }

        args.AddRange(sourceFiles);
        return args.ToArray();
    }

    private static IReadOnlyList<string> CompilerReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Runtime.Extensions.dll",
            "System.Console.dll",
            "netstandard.dll",
            "FSharp.Core.dll",
        };

        var refs = tpa
            .Where(path => wanted.Contains(Path.GetFileName(path)))
            .ToList();

        var fsharpCore = typeof(Microsoft.FSharp.Core.FSharpOption<>).Assembly.Location;
        if (refs.TrueForAll(path => !path.Equals(fsharpCore, StringComparison.OrdinalIgnoreCase)))
        {
            refs.Add(fsharpCore);
        }

        return refs;
    }

    // ---------- signature formatting (copied from FSharpSymbolQueryService.cs) ----------

    public static string EntityFullName(FSharpEntity entity)
    {
        if (entity.TryFullName is { } fullName && !string.IsNullOrWhiteSpace(fullName.Value))
        {
            return fullName.Value;
        }

        return string.IsNullOrWhiteSpace(entity.AccessPath) || entity.AccessPath is "."
            ? entity.DisplayName
            : entity.AccessPath + "." + entity.DisplayName;
    }

    public static string MemberFullName(FSharpEntity owner, FSharpMemberOrFunctionOrValue member)
    {
        member = ResolveAccessor(member, owner) ?? member;

        string baseName;
        if (member.IsConstructor)
        {
            baseName = EntityFullName(owner);
        }
        else if (!string.IsNullOrWhiteSpace(member.FullName) && member.FullName.Contains('.', StringComparison.Ordinal))
        {
            baseName = member.FullName;
        }
        else
        {
            var ownerName = EntityFullName(owner);
            var leaf = string.IsNullOrWhiteSpace(member.FullName) ? member.DisplayName : member.FullName;
            baseName = string.IsNullOrWhiteSpace(ownerName) ? leaf : ownerName + "." + leaf;
        }

        var signature = FormatParameterSignature(member);
        return string.IsNullOrEmpty(signature) ? baseName : baseName + signature;
    }

    private static FSharpMemberOrFunctionOrValue? ResolveAccessor(
        FSharpMemberOrFunctionOrValue member,
        FSharpEntity owner)
    {
        if (!member.IsPropertyGetterMethod && !member.IsPropertySetterMethod)
        {
            return null;
        }

        try
        {
            return owner.MembersFunctionsAndValues.FirstOrDefault(candidate =>
                candidate.IsProperty &&
                ((candidate.HasGetterMethod && SameAccessor(candidate.GetterMethod, member)) ||
                 (candidate.HasSetterMethod && SameAccessor(candidate.SetterMethod, member))));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameAccessor(FSharpMemberOrFunctionOrValue? accessor, FSharpMemberOrFunctionOrValue member) =>
        accessor is not null &&
        (accessor.Equals(member) ||
         (string.Equals(accessor.FullName, member.FullName, StringComparison.Ordinal) &&
          string.Equals(accessor.DisplayName, member.DisplayName, StringComparison.Ordinal)));

    private static string FormatParameterSignature(FSharpMemberOrFunctionOrValue member)
    {
        try
        {
            var groups = member.CurriedParameterGroups;
            if (groups.Count == 0)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var group in groups)
            {
                var formatted = group.Select(static parameter => FormatFSharpType(parameter.Type)).ToList();
                if (formatted is ["Microsoft.FSharp.Core.Unit"])
                {
                    formatted.Clear();
                }

                builder.Append('(');
                builder.Append(string.Join(",", formatted));
                builder.Append(')');
            }

            return builder.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static string FormatReturnType(FSharpMemberOrFunctionOrValue member)
    {
        try
        {
            var parameter = member.ReturnParameter;
            return parameter is null ? "?" : FormatFSharpType(parameter.Type);
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string FormatFSharpType(FSharpType type)
    {
        try
        {
            while (type.IsAbbreviation)
            {
                type = type.AbbreviatedType;
            }

            if (type.IsGenericParameter)
            {
                return "'" + type.GenericParameter.Name;
            }

            var generic = type.GenericArguments;
            if (type.IsFunctionType && generic.Count == 2)
            {
                return FormatFSharpType(generic[0]) + "->" + FormatFSharpType(generic[1]);
            }

            if (type.IsTupleType && generic.Count > 0)
            {
                return string.Join("*", generic.Select(FormatFSharpType));
            }

            if (type.HasTypeDefinition)
            {
                var definition = type.TypeDefinition;
                if (definition.IsArrayType)
                {
                    var brackets = definition.ArrayRank <= 1 ? "[]" : "[" + new string(',', definition.ArrayRank - 1) + "]";
                    return (generic.Count > 0 ? FormatFSharpType(generic[0]) : "?") + brackets;
                }

                var name = StripArity(EntityFullName(definition));
                return generic.Count == 0
                    ? name
                    : name + "<" + string.Join(",", generic.Select(FormatFSharpType)) + ">";
            }

            return type.ToString() ?? "?";
        }
        catch (Exception)
        {
            return type.ToString() ?? "?";
        }
    }

    private static string StripArity(string name)
    {
        var index = name.IndexOf('`');
        while (index >= 0)
        {
            var end = index + 1;
            while (end < name.Length && char.IsDigit(name[end]))
            {
                end++;
            }

            name = string.Concat(name.AsSpan(0, index), name.AsSpan(end));
            index = name.IndexOf('`', index);
        }

        return name;
    }

    // ---------- symbol matching (adapted from SameSymbol in Analysis.cs) ----------

    /// <summary>[COPIED] SameSymbol with the catalog item stubbed to its signature/display strings.</summary>
    public static bool SameSymbol(FSharpSymbol symbol, CatalogItem item)
    {
        if (symbol is FSharpMemberOrFunctionOrValue member)
        {
            if (member.DeclaringEntity is not { } declaringEntity)
            {
                return false;
            }

            var owner = declaringEntity.Value;
            var resolved = ResolveAccessor(member, owner) ?? member;
            var key = MemberFullName(owner, resolved);
            if (string.Equals(key, item.SignatureQualifiedName, StringComparison.Ordinal)
                || string.Equals(key + ":" + FormatReturnType(resolved), item.SignatureQualifiedName, StringComparison.Ordinal))
            {
                return true;
            }

            var loc = resolved.DeclarationLocation;
            return string.Equals(
                key + "@" + loc.StartLine + ":" + loc.StartColumn,
                item.SignatureQualifiedName,
                StringComparison.Ordinal);
        }

        var name = symbol is FSharpEntity entity ? EntityFullName(entity) : symbol.FullName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return string.Equals(name, item.SignatureQualifiedName, StringComparison.Ordinal);
        }

        return string.Equals(symbol.DisplayName, item.DisplayName, StringComparison.Ordinal)
            && SameDeclarationFile(symbol, item);
    }

    private static bool SameDeclarationFile(FSharpSymbol symbol, CatalogItem item)
    {
        if (symbol.DeclarationLocation is not { } declarationLocation)
        {
            return false;
        }

        var declaredFile = declarationLocation.Value.FileName;
        return item.LocationPaths.Any(path => SameDocumentPath(path, declaredFile));
    }

    private static bool SameDocumentPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>[COPIED] Analysis.cs: enclosing definition used for caller attribution.</summary>
    public static FSharpSymbolUse? FindEnclosingDefinition(
        IReadOnlyList<FSharpSymbolUse> definitions,
        FSharpSymbolUse call,
        CatalogItem callee)
    {
        FSharpSymbolUse? best = null;
        foreach (var definition in definitions)
        {
            if (SameSymbol(definition.Symbol, callee) ||
                !string.Equals(definition.FileName, call.FileName, StringComparison.OrdinalIgnoreCase) ||
                !RangeStartsOnOrBefore(definition.Range, call.Range))
            {
                continue;
            }

            if (best is null || RangeStartsOnOrBefore(best.Range, definition.Range))
            {
                best = definition;
            }
        }

        return best;
    }

    private static bool RangeStartsOnOrBefore(FcsRange left, FcsRange right) =>
        left.StartLine < right.StartLine ||
        (left.StartLine == right.StartLine && left.StartColumn <= right.StartColumn);

    /// <summary>Build the product-style signature for a symbol use's symbol (catalog-entry equivalent).</summary>
    public static string? SignatureOf(FSharpSymbol symbol)
    {
        if (symbol is FSharpMemberOrFunctionOrValue member)
        {
            if (member.DeclaringEntity is not { } declaringEntity)
            {
                return null;
            }

            return MemberFullName(declaringEntity.Value, ResolveAccessor(member, declaringEntity.Value) ?? member);
        }

        return symbol is FSharpEntity entity ? EntityFullName(entity) : symbol.FullName;
    }

    // ---------- range → offset (copied) ----------

    public static (int Start, int Length) ToSpan(string text, FcsRange range)
    {
        var start = OffsetOf(text, range.StartLine, range.StartColumn);
        var end = OffsetOf(text, range.EndLine, range.EndColumn);
        if (end < start)
        {
            end = start;
        }

        return (start, end - start);
    }

    private static int OffsetOf(string text, int line1Based, int column0Based)
    {
        var line = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (line == line1Based)
            {
                return Math.Min(text.Length, i + Math.Max(0, column0Based));
            }

            if (text[i] == '\n')
            {
                line++;
            }
        }

        return text.Length;
    }

    // ---------- rename slicing (copied from FSharpSymbolQueryService.Rename.cs) ----------

    public static bool IsSimpleIdentifierUse(string slice, string oldName)
    {
        if (string.Equals(slice, oldName, StringComparison.Ordinal))
        {
            return true;
        }

        return slice.EndsWith("." + oldName, StringComparison.Ordinal) &&
               slice.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '.');
    }

    public static string? LastIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var paren = trimmed.IndexOfAny(['(', '<', ' ']);
        if (paren > 0)
        {
            trimmed = trimmed[..paren];
        }

        var last = trimmed.LastIndexOf('.');
        return last >= 0 ? trimmed[(last + 1)..] : trimmed;
    }

    public static string ApplyReplacements(string text, List<(int Start, int Length)> spans, string newName)
    {
        var unique = spans
            .Distinct()
            .OrderByDescending(s => s.Start)
            .ToList();
        var builder = new System.Text.StringBuilder(text);
        foreach (var (start, length) in unique)
        {
            builder.Remove(start, length);
            builder.Insert(start, newName);
        }

        return builder.ToString();
    }
}
