using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotNetMcp.Tests;

internal static class PackageIdentityGate
{
    public static string? Evaluate(string csprojXml, string serverJson, string changelog)
    {
        var csprojMatch = Regex.Match(csprojXml, @"<Version>([^<]+)</Version>");
        if (!csprojMatch.Success)
        {
            return "csproj <Version> missing";
        }

        var version = csprojMatch.Groups[1].Value;
        using var doc = JsonDocument.Parse(serverJson);
        if (!doc.RootElement.TryGetProperty("version", out var jsonVersionElement))
        {
            return "server.json version missing";
        }

        var jsonVersion = jsonVersionElement.GetString();
        if (jsonVersion != version)
        {
            return $"server.json version '{jsonVersion}' != csproj '{version}'";
        }

        var releasedMatch = Regex.Match(changelog, @"^## (\d+\.\d+\.\d+)\b", RegexOptions.Multiline);
        if (!releasedMatch.Success)
        {
            return "CHANGELOG.md missing ## MAJOR.MINOR.PATCH heading";
        }

        var released = releasedMatch.Groups[1].Value;
        var unreleased = Regex.Match(
            changelog,
            @"^## Unreleased\b(.*?)(?=^## |\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        var hasProductEntries = unreleased.Success
            && Regex.IsMatch(unreleased.Groups[1].Value, @"^- ", RegexOptions.Multiline);

        if (hasProductEntries && version == released)
        {
            return $"Unreleased has product entries but Version '{version}' still equals previous release '{released}'";
        }

        if (!hasProductEntries && released != version)
        {
            return $"CHANGELOG version '{released}' != csproj '{version}'";
        }

        return null;
    }
}
