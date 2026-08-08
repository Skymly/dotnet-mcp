using System.Text.Json;

namespace S3.Core;

public static class SoftBudgetPager
{
    /// <summary>
    /// Returns a page of synthetic items under a soft time budget, with a continuation cursor.
    /// </summary>
    public static SoftBudgetPageDto Page(string? cursor, int pageSize, int totalItems, TimeSpan budget, TimeSpan simulatedItemCost)
    {
        pageSize = Math.Max(1, pageSize);
        totalItems = Math.Max(0, totalItems);
        var start = 0;
        if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsed))
        {
            start = Math.Max(0, parsed);
        }

        var items = new List<string>();
        var spent = TimeSpan.Zero;
        var index = start;
        while (index < totalItems && items.Count < pageSize)
        {
            if (spent + simulatedItemCost > budget && items.Count > 0)
            {
                break;
            }

            items.Add($"item-{index}");
            spent += simulatedItemCost;
            index++;
        }

        var truncated = index < totalItems;
        return new SoftBudgetPageDto
        {
            Items = items,
            Truncated = truncated,
            NextCursor = truncated ? index.ToString() : null,
            Message = truncated
                ? $"Soft budget reached after {items.Count} item(s). Pass nextCursor to continue; do not retry from scratch."
                : $"Returned {items.Count} item(s); complete."
        };
    }

    public static string ToJson(SoftBudgetPageDto dto) =>
        JsonSerializer.Serialize(dto, JsonOptions.Default);
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
