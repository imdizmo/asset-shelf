using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AssetShelf;

/// <summary>A turntable of an asset: frames rendered once by headless Blender (blender/turntable.py), kept in
/// ~/.cache/asset-shelf/turntables (at most <see cref="CacheMax"/> bytes, oldest dropped).</summary>
public static class Turntable
{
    public const long CacheMax = 1L << 30;

    private static string Root => Path.Combine(Path.GetDirectoryName(Sender.Outbox())!, "turntables");

    /// <summary>The frames, rendering them first when they are not in the cache.</summary>
    public static async Task<List<string>> FramesAsync(Item item, IReadOnlyList<string> files, string? texDir, bool unreal, string blenderDir)
    {
        string dir = Sender.CacheDir(Root, item);
        if (!File.Exists(Path.Combine(dir, ".done")))
        {
            Directory.CreateDirectory(dir);
            string job = Path.Combine(dir, "job.json");
            await File.WriteAllTextAsync(job, JsonSerializer.Serialize(new { name = item.Name, files, textures = texDir, unreal, @out = dir, frames = 24, size = 384 })).ConfigureAwait(false);
            using var step = Log.Time("turntable", $"{item.Name}: 24 frames");
            var psi = new ProcessStartInfo("systemd-run")
            {
                ArgumentList = { "--user", "--scope", "-q", "-p", "MemoryMax=6G", "-p", "MemorySwapMax=0",
                    "blender", "-b", "--factory-startup", "-P", Path.Combine(blenderDir, "turntable.py"), "--", job },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process p = Process.Start(psi)!;
            string err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (!File.Exists(Path.Combine(dir, ".done")))
            {
                throw new InvalidOperationException("turntable failed: " + err[Math.Max(0, err.Length - 300)..]);
            }

            Trim(dir);
        }

        Directory.SetLastWriteTime(dir, DateTime.Now);
        return [.. Directory.GetFiles(dir, "*.png").Order(StringComparer.Ordinal)];
    }

    private static void Trim(string keep)
    {
        var dirs = Directory.GetDirectories(Root).SelectMany(Directory.GetDirectories)
            .Select(d => (Dir: d, Size: Directory.GetFiles(d).Sum(f => new FileInfo(f).Length), At: Directory.GetLastWriteTime(d)))
            .OrderBy(d => d.At).ToList();
        long total = dirs.Sum(d => d.Size);
        foreach (var d in dirs)
        {
            if (total <= CacheMax)
            {
                break;
            }

            if (d.Dir != keep && Path.GetDirectoryName(Path.GetDirectoryName(d.Dir)) == Root)
            {
                Directory.Delete(d.Dir, recursive: true);
                total -= d.Size;
            }
        }
    }
}

/// <summary>Plays turntable frames; drag sideways to turn the model by hand (it stops turning by itself then).</summary>
public sealed class TurntableView : Image
{
    private readonly List<Bitmap> _frames;
    private readonly DispatcherTimer _spin = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private int _at;
    private double? _dragFrom;
    private int _dragStart;

    public TurntableView(IEnumerable<string> frames)
    {
        _frames = [.. frames.Select(f => new Bitmap(f))];
        Stretch = Stretch.Uniform;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Source = _frames.FirstOrDefault();
        _spin.Tick += (_, _) => Show(_at + 1);
        AttachedToVisualTree += (_, _) => _spin.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _spin.Stop();
            Source = null;
            foreach (Bitmap b in _frames)
            {
                b.Dispose();
            }
        };
        PointerPressed += (_, e) =>
        {
            _spin.Stop();
            _dragFrom = e.GetPosition(this).X;
            _dragStart = _at;
            e.Pointer.Capture(this);
        };
        PointerMoved += (_, e) =>
        {
            if (_dragFrom is { } x0)
            {
                Show(_dragStart - (int)((e.GetPosition(this).X - x0) / 12));
            }
        };
        PointerReleased += (_, e) =>
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
        };
    }

    private void Show(int i)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        _at = ((i % _frames.Count) + _frames.Count) % _frames.Count;
        Source = _frames[_at];
    }
}
