using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace DotNetMcp.Core;

/// <summary>
/// Changed solution from a code action, or the exception type when the provider threw.
/// A null solution with a null exception type means the action produced no document change.
/// </summary>
public readonly record struct CodeActionApplyResult(Solution? Solution, string? ProviderExceptionType);

/// <summary>
/// CodeAction → changed documents. First-party / parameterless provider load,
/// nested-action flatten, ApplyChangesOperation, and handwritten slices. Shared by Diagnostic fix
/// and Code Refactoring. Diff stays in HandwrittenDocumentDiff.
/// </summary>
public static class CodeActionDocuments
{
    private static readonly ConcurrentDictionary<(string Language, Type ProviderType), object> ProvidersByLanguage = new();

    public static IReadOnlyList<TProvider> GetProviders<TProvider>(string language)
        where TProvider : class
    {
        return (IReadOnlyList<TProvider>)ProvidersByLanguage.GetOrAdd(
            (language, typeof(TProvider)),
            _ => LoadProviders<TProvider>(language));
    }

    public static IEnumerable<CodeAction> Flatten(CodeAction action)
    {
        var nested = action.NestedActions;
        return nested.Length == 0 ? [action] : nested.SelectMany(Flatten);
    }

    public static async Task<CodeActionApplyResult> ApplyActionAsync(CodeAction action, CancellationToken cancellationToken)
    {
        try
        {
            var operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            return new CodeActionApplyResult(
                operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution,
                ProviderExceptionType: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CodeActionApplyResult(null, ex.GetType().Name);
        }
    }

    public static async Task<(IReadOnlyList<RenameDocumentSlice>? Documents, SymbolQueryError? Error)> ToHandwrittenSlicesAsync(
        Solution before,
        Solution? after,
        Func<SymbolQueryError> applyFailed,
        Func<SymbolQueryError> generatedRefused,
        Func<SymbolQueryError> addOrRemoveRefused,
        CancellationToken cancellationToken)
    {
        if (after is null)
        {
            return (null, applyFailed());
        }

        var diff = await HandwrittenDocumentDiff
            .FromSolutionsAsync(before, after, cancellationToken)
            .ConfigureAwait(false);
        return DecideSlices(diff, applyFailed, generatedRefused, addOrRemoveRefused);
    }

    internal static (IReadOnlyList<RenameDocumentSlice>? Documents, SymbolQueryError? Error) DecideSlices(
        HandwrittenDiff diff,
        Func<SymbolQueryError> applyFailed,
        Func<SymbolQueryError> generatedRefused,
        Func<SymbolQueryError> addOrRemoveRefused)
    {
        if (diff.AddsOrRemovesDocuments)
        {
            return (null, addOrRemoveRefused());
        }

        if (diff.TouchedGenerated)
        {
            return (null, generatedRefused());
        }

        if (diff.Slices.Count == 0)
        {
            return (null, applyFailed());
        }

        return (diff.Slices, null);
    }

    private static IReadOnlyList<TProvider> LoadProviders<TProvider>(string language)
        where TProvider : class
    {
        var assemblyName = language switch
        {
            LanguageNames.CSharp => "Microsoft.CodeAnalysis.CSharp.Features",
            LanguageNames.VisualBasic => "Microsoft.CodeAnalysis.VisualBasic.Features",
            _ => null
        };
        if (assemblyName is null)
        {
            return [];
        }

        Assembly assembly;
        try
        {
            assembly = Assembly.Load(assemblyName);
        }
        catch (Exception)
        {
            return [];
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(static t => t is not null).Cast<Type>().ToArray();
        }

        var list = new List<TProvider>();
        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(TProvider).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is TProvider provider)
                {
                    list.Add(provider);
                }
            }
            catch (Exception)
            {
                // Some parameterless types still throw in the ctor.
            }
        }

        return list;
    }
}
