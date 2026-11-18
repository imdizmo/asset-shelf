using System.Diagnostics;

namespace AssetShelf;

/// <summary>What the app is doing, with timings. Always recorded (the last <see cref="Max"/> lines), so the debug window
/// shows what happened before it was opened. Safe to call from any thread.</summary>
public static class Log
{
    public readonly record struct Entry(DateTime At, string Area, string Text, double? Ms);

    public const int Max = 5000;
    private static readonly Queue<Entry> Entries = new();
    private static readonly object Gate = new();

    /// <summary>Raised on the writing thread for every new line.</summary>
    public static event Action<Entry>? Added;

    public static void Write(string area, string text, double? ms = null)
    {
        var e = new Entry(DateTime.Now, area, text, ms);
        lock (Gate)
        {
            Entries.Enqueue(e);
            while (Entries.Count > Max)
            {
                Entries.Dequeue();
            }
        }

        Added?.Invoke(e);
    }

    public static List<Entry> Snapshot()
    {
        lock (Gate)
        {
            return [.. Entries];
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }

    /// <summary>Times a step: <c>using (Log.Time("grid", "refresh")) { ... }</c> writes one line with its duration when
    /// disposed. <see cref="Step.Then"/> can change the text once the result is known.</summary>
    public static Step Time(string area, string what) => new(area, what);

    public sealed class Step(string area, string what) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private string _what = what;

        public double Ms => _watch.Elapsed.TotalMilliseconds;

        public void Then(string what) => _what = what;

        public void Dispose() => Write(area, _what, _watch.Elapsed.TotalMilliseconds);
    }
}
