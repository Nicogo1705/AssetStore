// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;

namespace AssetStore.Core.Indexing;

/// <summary>
/// Maintains each asset's rolling daily stars history across index rebuilds: the history lives in
/// the index itself and is carried over from the previous index file, so the "trending" sort
/// (7-day star delta) needs no server and no extra storage.
/// </summary>
public static class StarsHistory
{
    /// <summary>Days of history kept (enough for a 7-day delta with margin).</summary>
    public const int WindowDays = 30;

    /// <summary>
    /// Returns <paramref name="index"/> with every asset's snapshots carried over from
    /// <paramref name="previous"/>, today's count appended (replacing an earlier same-day entry),
    /// and entries older than <see cref="WindowDays"/> pruned. Idempotent within a day.
    /// </summary>
    public static IndexLock Apply(IndexLock index, IndexLock? previous)
    {
        var prevById = previous?.Assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var today = index.GeneratedAt.Length >= 10 ? index.GeneratedAt[..10] : index.GeneratedAt;
        var cutoff = DateOnly.TryParse(today, out var t)
            ? t.AddDays(-WindowDays).ToString("yyyy-MM-dd")
            : "";

        var assets = index.Assets.Select(asset =>
        {
            IReadOnlyList<StarsSnapshot> carried =
                prevById is not null && prevById.TryGetValue(asset.Id, out var prev)
                    ? prev.StarsSnapshots
                    : asset.StarsSnapshots; // incremental builds may already carry them

            var history = carried
                .Where(s => string.CompareOrdinal(s.Date, cutoff) >= 0
                    && !string.Equals(s.Date, today, StringComparison.Ordinal))
                .ToList();

            if (asset.Stars is { } stars)
            {
                history.Add(new StarsSnapshot { Date = today, Stars = stars });
            }

            return asset with { StarsSnapshots = history };
        }).ToList();

        return index with { Assets = assets };
    }

    /// <summary>
    /// The star delta over the last ~7 days: latest snapshot minus the newest snapshot at least
    /// 7 days older (or the oldest available). Zero when there is no history.
    /// </summary>
    public static int SevenDayDelta(IndexedAsset asset)
    {
        var snapshots = asset.StarsSnapshots;
        if (snapshots.Count == 0)
        {
            return 0;
        }

        var latest = snapshots[^1];
        var cutoff = DateOnly.TryParse(latest.Date, out var d)
            ? d.AddDays(-7).ToString("yyyy-MM-dd")
            : latest.Date;

        var baseline = snapshots[0];
        foreach (var snapshot in snapshots)
        {
            if (string.CompareOrdinal(snapshot.Date, cutoff) <= 0)
            {
                baseline = snapshot;
            }
        }

        return latest.Stars - baseline.Stars;
    }
}
