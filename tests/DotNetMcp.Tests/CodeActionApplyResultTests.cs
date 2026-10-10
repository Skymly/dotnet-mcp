using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace DotNetMcp.Tests;

public class CodeActionApplyResultTests
{
    [Fact]
    public async Task provider_throw_is_not_reported_as_no_change()
    {
        var thrown = await CodeActionDocuments.ApplyActionAsync(new ThrowingAction(), CancellationToken.None);
        Assert.Equal(nameof(InvalidOperationException), thrown.ProviderExceptionType);
        Assert.Null(thrown.Solution);

        var empty = await CodeActionDocuments.ApplyActionAsync(new EmptyAction(), CancellationToken.None);
        Assert.Null(empty.ProviderExceptionType);
        Assert.Null(empty.Solution);
    }

    private sealed class ThrowingAction : CodeAction
    {
        public override string Title => "Throwing";

        protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("provider failed");
    }

    private sealed class EmptyAction : CodeAction
    {
        public override string Title => "Empty";

        protected override Task<IEnumerable<CodeActionOperation>> ComputeOperationsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<CodeActionOperation>>([]);
    }
}