using System;
using System.Collections.Generic;

namespace AutoTreasure;

public enum ActivityLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public readonly record struct ActivityEntry(DateTime Time, ActivityLevel Level, string Message);

/// <summary>
/// Small bounded in-memory log surfaced in the main window.
/// </summary>
public sealed class ActivityLog
{
    private const int MaxEntries = 400;
    private readonly LinkedList<ActivityEntry> entries = new();

    public event Action? Changed;

    public IReadOnlyCollection<ActivityEntry> Entries => this.entries;

    public void Add(string message, ActivityLevel level = ActivityLevel.Info)
    {
        this.entries.AddLast(new ActivityEntry(DateTime.Now, level, message));
        while (this.entries.Count > MaxEntries)
        {
            this.entries.RemoveFirst();
        }

        this.Changed?.Invoke();
    }

    public void Success(string message) => this.Add(message, ActivityLevel.Success);

    public void Warn(string message) => this.Add(message, ActivityLevel.Warning);

    public void Error(string message) => this.Add(message, ActivityLevel.Error);

    public void Clear()
    {
        this.entries.Clear();
        this.Changed?.Invoke();
    }
}
