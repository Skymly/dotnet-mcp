using System.Reflection;
using System.Runtime.CompilerServices;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class PagedMessageOptionalityTests
{
    [Fact]
    public void every_paged_dto_has_an_optional_non_required_message()
    {
        var paged = typeof(PolicyErrorDto).Assembly.GetTypes()
            .Where(t => t.GetProperty("NextCursor") is not null)
            .ToArray();
        Assert.NotEmpty(paged);

        var context = new NullabilityInfoContext();
        foreach (var type in paged)
        {
            var message = type.GetProperty("Message");
            Assert.NotNull(message);
            Assert.Equal(typeof(string), message!.PropertyType);
            Assert.False(
                message.IsDefined(typeof(RequiredMemberAttribute), inherit: true),
                $"{type.Name}.Message must not be required.");
            Assert.Equal(NullabilityState.Nullable, context.Create(message).WriteState);
        }
    }
}
