namespace CodexUsageTray;

public enum ModelBreakdownSort
{
    Model,
    Effort,
    Tokens,
    Sessions,
    Cost,
    Share
}

public static class ModelBreakdownOrdering
{
    public static IEnumerable<ModelUsageBreakdown> Sort(
        IEnumerable<ModelUsageBreakdown> rows,
        ModelBreakdownSort mode,
        bool descending)
    {
        return mode switch
        {
            ModelBreakdownSort.Effort when descending => rows
                .OrderBy(item => EffortRank(item.Effort) == 0)
                .ThenByDescending(item => EffortRank(item.Effort))
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Effort => rows
                .OrderBy(item => EffortRank(item.Effort) == 0)
                .ThenBy(item => EffortRank(item.Effort))
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Model when descending => rows
                .OrderByDescending(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.Effort, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Model => rows
                .OrderBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Effort, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Cost when descending => rows
                .OrderBy(item => item.DollarAmount is null)
                .ThenByDescending(item => item.DollarAmount)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Cost => rows
                .OrderBy(item => item.DollarAmount is null)
                .ThenBy(item => item.DollarAmount)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Sessions when descending => rows
                .OrderByDescending(item => item.Sessions)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Sessions => rows
                .OrderBy(item => item.Sessions)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Share when descending => rows
                .OrderByDescending(item => item.TokenSharePercent)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            ModelBreakdownSort.Share => rows
                .OrderBy(item => item.TokenSharePercent)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            _ when descending => rows
                .OrderByDescending(item => item.TotalTokens)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase),
            _ => rows
                .OrderBy(item => item.TotalTokens)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
        };
    }

    public static int EffortRank(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "ultra" => 6,
        "xhigh" or "extra high" or "extra_high" => 5,
        "max" => 4,
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };
}
