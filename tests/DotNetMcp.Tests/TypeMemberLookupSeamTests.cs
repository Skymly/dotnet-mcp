using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

/// <summary>
/// In-process Core inner API (ADR-0001 §3) — XAML Binding-path walking without MCP DTO N+1.
/// </summary>
public class TypeMemberLookupSeamTests
{
    [Fact]
    public async Task type_handle_resolves_instance_property_by_name_with_member_type()
    {
        var (session, symbols) = OpenSymbols();
        using (session)
        {
            var handle = await ResolveHandleAsync(symbols, session, "SampleLib.Calculator");

            var (lookup, error) = await symbols.LookupTypeMemberAsync(session, handle, "Mode");

            Assert.Null(error);
            Assert.NotNull(lookup);
            Assert.Equal("Mode", lookup!.Member.Name);
            Assert.Equal(SymbolKind.Property, lookup.Member.Kind);
            Assert.Equal("Int32", lookup.MemberType.Name);
        }
    }

    [Fact]
    public async Task type_handle_resolves_instance_field_by_name_with_member_type()
    {
        var (session, symbols) = OpenViewModels();
        using (session)
        {
            var handle = await ResolveHandleAsync(symbols, session, "SampleApp.Customer");

            var (lookup, error) = await symbols.LookupTypeMemberAsync(session, handle, "Nickname");

            Assert.Null(error);
            Assert.NotNull(lookup);
            Assert.Equal("Nickname", lookup!.Member.Name);
            Assert.Equal(SymbolKind.Field, lookup.Member.Kind);
            Assert.Equal("String", lookup.MemberType.Name);
        }
    }

    [Fact]
    public async Task nested_path_walks_member_types_without_handle_round_trip()
    {
        var (session, symbols) = OpenViewModels();
        using (session)
        {
            var handle = await ResolveHandleAsync(symbols, session, "SampleApp.Customer");

            var (home, homeError) = await symbols.LookupTypeMemberAsync(session, handle, "Home");
            Assert.Null(homeError);
            Assert.NotNull(home);

            var (city, cityError) = symbols.LookupTypeMember(home!.Project, home.MemberType, "City");
            Assert.Null(cityError);
            Assert.NotNull(city);
            Assert.Equal("City", city!.Member.Name);
            Assert.Equal("String", city.MemberType.Name);

            var cityHandle = symbols.FormatHandle(city.Project, city.Member);
            Assert.True(SymbolHandle.TryParse(cityHandle, out var parsed, out _), cityHandle);
            Assert.Equal("csharp", parsed!.Language);
            Assert.Contains("City", parsed.SignatureQualifiedName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task inherited_property_is_found_on_derived_type_handle()
    {
        var (session, symbols) = OpenViewModels();
        using (session)
        {
            var handle = await ResolveHandleAsync(symbols, session, "SampleApp.Customer");

            var (lookup, error) = await symbols.LookupTypeMemberAsync(session, handle, "Name");

            Assert.Null(error);
            Assert.NotNull(lookup);
            Assert.Equal("Name", lookup!.Member.Name);
            Assert.Equal(SymbolKind.Property, lookup.Member.Kind);
            Assert.Equal("String", lookup.MemberType.Name);
        }
    }

    [Fact]
    public async Task missing_member_is_distinguishable_from_invalid_handle()
    {
        var (session, symbols) = OpenViewModels();
        using (session)
        {
            var handle = await ResolveHandleAsync(symbols, session, "SampleApp.Customer");

            var (missing, missingError) = await symbols.LookupTypeMemberAsync(session, handle, "NoSuchProperty");
            Assert.Null(missing);
            Assert.IsType<MemberNotFoundError>(missingError);
            Assert.Equal(SymbolQueryErrorCodes.MemberNotFound, missingError!.Code);
            Assert.Contains("Binding path", missingError.SuggestedAction, StringComparison.OrdinalIgnoreCase);

            var bad = handle[..^1] + (handle[^1] == '0' ? '1' : '0');
            var (invalid, invalidError) = await symbols.LookupTypeMemberAsync(session, bad, "NoSuchProperty");
            Assert.Null(invalid);
            Assert.IsType<InvalidSymbolHandleError>(invalidError);
            Assert.NotEqual(missingError.Code, invalidError!.Code);
        }
    }

    [Fact]
    public async Task method_name_is_member_not_found_not_a_property()
    {
        var (session, symbols) = OpenSymbols();
        using (session)
        {
            var handle = await ResolveHandleAsync(symbols, session, "SampleLib.Calculator");

            var (lookup, error) = await symbols.LookupTypeMemberAsync(session, handle, "Add");

            Assert.Null(lookup);
            Assert.IsType<MemberNotFoundError>(error);
            Assert.Equal(SymbolQueryErrorCodes.MemberNotFound, error!.Code);
        }
    }

    [Fact]
    public async Task invalid_handle_checksum_is_invalid_symbol_handle()
    {
        var (session, symbols) = OpenSymbols();
        using (session)
        {
            var handle = await ResolveHandleAsync(symbols, session, "SampleLib.Calculator");
            var bad = handle[..^1] + (handle[^1] == '0' ? '1' : '0');

            var (lookup, error) = await symbols.LookupTypeMemberAsync(session, bad, "Mode");

            Assert.Null(lookup);
            Assert.IsType<InvalidSymbolHandleError>(error);
            Assert.Equal(SymbolQueryErrorCodes.InvalidSymbolHandle, error!.Code);
            Assert.Contains("symbol_resolve", error.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task well_formed_missing_handle_is_symbol_not_found()
    {
        var (session, symbols) = OpenSymbols();
        using (session)
        {
            var projectId = session.Solution.Projects.Single().Id.Id.ToString("D");
            var ghost = SymbolHandle.Create(LanguageAdapters.CSharpLanguage, projectId, "SampleLib.DoesNotExist");

            var (lookup, error) = await symbols.LookupTypeMemberAsync(session, ghost.Format(), "Mode");

            Assert.Null(lookup);
            Assert.IsType<SymbolNotFoundError>(error);
            Assert.Equal(SymbolQueryErrorCodes.SymbolNotFound, error!.Code);
            Assert.NotEqual(SymbolQueryErrorCodes.MemberNotFound, error.Code);
            Assert.NotEqual(SymbolQueryErrorCodes.InvalidSymbolHandle, error.Code);
        }
    }

    [Fact]
    public async Task method_handle_is_not_a_type_for_member_lookup()
    {
        var (session, symbols) = OpenSymbols();
        using (session)
        {
            var (resolved, resolveError) = await symbols.ResolveByNameAsync(session, "SampleLib.Calculator.Clear");
            Assert.Null(resolveError);
            Assert.NotNull(resolved);

            var (lookup, error) = await symbols.LookupTypeMemberAsync(session, resolved!.Handle, "Mode");

            Assert.Null(lookup);
            Assert.IsType<SymbolNotFoundError>(error);
            Assert.Contains("type", error!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(SymbolQueryErrorCodes.MemberNotFound, error.Code);
        }
    }

    private static (WorkspaceSession Session, RoslynLanguageAdapter Symbols) OpenSymbols()
    {
        var loaded = FakeSolutionLoader.CreateSymbolsLoaded(@"C:\fake\SampleLib.csproj");
        return (new WorkspaceSession(loaded, epoch: 1), new RoslynLanguageAdapter(new GeneratorQueryService()));
    }

    private static (WorkspaceSession Session, RoslynLanguageAdapter Symbols) OpenViewModels()
    {
        var loaded = FakeSolutionLoader.CreateViewModelLoaded();
        return (new WorkspaceSession(loaded, epoch: 1), new RoslynLanguageAdapter(new GeneratorQueryService()));
    }

    private static async Task<string> ResolveHandleAsync(
        RoslynLanguageAdapter symbols,
        IWorkspaceSession session,
        string name)
    {
        var (resolved, error) = await symbols.ResolveByNameAsync(session, name);
        Assert.Null(error);
        Assert.NotNull(resolved);
        return resolved!.Handle;
    }
}
