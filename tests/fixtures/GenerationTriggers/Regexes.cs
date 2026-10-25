using System.Text.RegularExpressions;

namespace TriggerApp;

public partial class Regexes
{
    [GeneratedRegex("foo+")]
    public static partial Regex Foo();
}
