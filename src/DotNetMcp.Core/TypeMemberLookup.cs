using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// In-process member lookup for Binding-path walking (ADR-0001 inner API).
/// </summary>
internal sealed record TypeMemberLookup(
    ISymbol Member,
    ITypeSymbol MemberType,
    Project Project);
