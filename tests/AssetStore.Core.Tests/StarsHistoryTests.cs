// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Indexing;
using AssetStore.Core.Models;
using Xunit;

namespace AssetStore.Core.Tests;

public class StarsHistoryTests
{
    private static IndexedAsset Asset(string? addedAt, params (string Date, int Stars)[] snapshots) =>
        CatalogTestData.Asset("com.test.asset", "Test", "Scripts") with
        {
            AddedAt = addedAt,
            StarsSnapshots = snapshots.Select(s => new StarsSnapshot { Date = s.Date, Stars = s.Stars }).ToList(),
        };

    [Fact]
    public void NoHistory_IsZero()
    {
        Assert.Equal(0, StarsHistory.SevenDayDelta(Asset("2026-07-01")));
    }

    [Fact]
    public void NewlyListedAsset_CountsItsStarsAsTheTrend()
    {
        // The Avalonia-UI case: listed yesterday, one star present from the very first
        // snapshot. Oldest-as-baseline made this permanently 0 — it must trend with 1.
        var asset = Asset("2026-07-30", ("2026-07-30", 1), ("2026-07-31", 1));
        Assert.Equal(1, StarsHistory.SevenDayDelta(asset));
    }

    [Fact]
    public void ShortHistoryOnOldListing_FallsBackToOldestSnapshot()
    {
        // Listed long ago but history is short (pruned/restarted): no free boost,
        // only observed movement counts.
        var asset = Asset("2026-01-01", ("2026-07-30", 10), ("2026-07-31", 12));
        Assert.Equal(2, StarsHistory.SevenDayDelta(asset));
    }

    [Fact]
    public void FullWindow_UsesTheSevenDayBaseline()
    {
        var asset = Asset("2026-01-01",
            ("2026-07-20", 3), ("2026-07-24", 5), ("2026-07-28", 8), ("2026-07-31", 9));
        // Baseline = newest snapshot at least 7 days before the latest (2026-07-24 → 5).
        Assert.Equal(4, StarsHistory.SevenDayDelta(asset));
    }
}
