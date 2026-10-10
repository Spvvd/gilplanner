using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GilPlanner.Pricing;

/// <summary>The result of one popularity scan of a world.</summary>
public sealed record PopularScan(uint WorldId, DateTimeOffset ScannedAt, List<PopularItem> Items);

/// <summary>Keeps the latest popularity scans on disk so they survive restarts. Never throws on bad or missing files.</summary>
public sealed class PopularCache(string path)
{
    private const int MaxWorlds = 5;

    private readonly object sync = new();
    private readonly Dictionary<uint, PopularScan> scans = new();
    private bool loaded;

    public PopularScan? Get(uint worldId)
    {
        lock (sync)
        {
            EnsureLoaded();
            return scans.GetValueOrDefault(worldId);
        }
    }

    public void Store(PopularScan scan)
    {
        lock (sync)
        {
            EnsureLoaded();
            scans[scan.WorldId] = scan;

            // Keep only the most recently scanned worlds.
            foreach (var old in scans.Values.OrderByDescending(s => s.ScannedAt).Skip(MaxWorlds).ToList())
                scans.Remove(old.WorldId);

            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (loaded)
            return;

        loaded = true;
        try
        {
            if (!File.Exists(path))
                return;

            var stored = JsonSerializer.Deserialize<List<PopularScan>>(File.ReadAllText(path));
            foreach (var scan in stored ?? [])
            {
                // A hand-edited or damaged file could hold entries without a list; showing one would crash the tab.
                if (scan?.Items is { } items && items.All(item => item is not null))
                    scans[scan.WorldId] = scan;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            scans.Clear();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write to a temp file first so a crash cannot leave a half-written cache behind.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(scans.Values.ToList()));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is only a missed optimisation.
        }
    }
}
