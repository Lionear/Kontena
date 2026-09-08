using Kontena.App.ViewModels;

namespace Kontena.App.Tests;

/// <summary>
/// Ordering the cells of a server-rendered listing (KON-454). Every case here is one that ordinal
/// string order gets wrong, which is the whole reason this exists.
/// </summary>
public sealed class ResourceCellOrderTests
{
    private static string[] Sorted(params string[] cells) =>
        [.. cells.OrderBy(ResourceCellOrder.Of)];

    [Fact]
    public void An_age_sorts_by_how_long_ago_it_was_not_alphabetically()
    {
        // Ordinal order gives 12h, 3d, 45m, 9d — every one of them in the wrong place.
        Assert.Equal(["45m", "12h", "3d", "9d"], Sorted("3d", "45m", "9d", "12h"));
    }

    [Fact]
    public void A_compound_age_counts_both_of_its_parts()
    {
        Assert.Equal(["2d", "2d3h", "3d"], Sorted("3d", "2d3h", "2d"));
    }

    [Fact]
    public void A_number_sorts_as_a_number()
    {
        Assert.Equal(["0", "2", "9", "10", "17"], Sorted("10", "2", "17", "0", "9"));
    }

    [Fact]
    public void A_size_sorts_by_what_the_suffix_means()
    {
        Assert.Equal(["64Mi", "512Mi", "2Gi", "4Gi"], Sorted("2Gi", "512Mi", "4Gi", "64Mi"));
    }

    /// <summary>READY is sorted to find what is not ready, so 0/2 comes before a complete 1/1.</summary>
    [Fact]
    public void A_ratio_sorts_by_how_complete_it_is()
    {
        Assert.Equal(["0/2", "2/3", "1/1", "3/3"], Sorted("1/1", "0/2", "3/3", "2/3"));
    }

    [Fact]
    public void Text_sorts_case_insensitively()
    {
        Assert.Equal(["alpha", "Beta", "gamma"], Sorted("gamma", "alpha", "Beta"));
    }

    /// <summary>
    /// The case that would otherwise throw: one unparseable cell in an otherwise numeric column. A
    /// listing is not allowed to fall over because a CRD author printed "&lt;none&gt;".
    /// </summary>
    [Fact]
    public void A_column_of_numbers_holding_one_word_still_orders()
    {
        Assert.Equal(["1", "2", "<none>"], Sorted("2", "<none>", "1"));
    }

    [Fact]
    public void An_empty_cell_is_ordered_rather_than_skipped()
    {
        Assert.Equal(["", "beta"], Sorted("beta", ""));
        Assert.Equal(ResourceCellOrder.Of(""), ResourceCellOrder.Of(null));
    }

    /// <summary>
    /// Millicores read as a duration, because "120m" is the same shape as ninety minutes. Harmless:
    /// the rule is applied to every cell in the column, so the order it produces is still numeric.
    /// </summary>
    [Fact]
    public void Cpu_millicores_still_order_numerically()
    {
        Assert.Equal(["8m", "42m", "145m", "620m"], Sorted("145m", "620m", "8m", "42m"));
    }
}
