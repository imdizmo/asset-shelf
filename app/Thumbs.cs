using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AssetShelf;

/// <summary>Preview pictures, decoded small off the UI thread, at most <see cref="Budget"/> bytes kept (least recently used dropped).</summary>
public static class Thumbs
{
    public const long Budget = 256L << 20;  // decoded pixels kept, in bytes
    private static long _bytes;
    public const int Width = 160;
    private static readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bmp)>> Map = new(StringComparer.Ordinal);
    private static readonly LinkedList<(string Key, Bitmap Bmp)> Lru = new();
    private static readonly SemaphoreSlim Gate = new(4);

    /// <summary>Calls <paramref name="done"/> on the UI thread with the picture (null when it cannot be read).</summary>
    public static void Get(string? path, Action<Bitmap?> done, int width = Width)
    {
        if (path is null)
        {
            done(null);
            return;
        }

        string key = width == Width ? path : $"{path}|{width}";
        if (Map.TryGetValue(key, out var node))
        {
            Lru.Remove(node);
            Lru.AddFirst(node);
            done(node.Value.Bmp);
            return;
        }

        _ = Task.Run(async () =>
        {
            Bitmap? bmp = null;
            await Gate.WaitAsync().ConfigureAwait(false);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path))
                {
                    await Team.EnsureAsync(path).ConfigureAwait(false);  // a team library's picture, fetched on first sight
                }

                await using FileStream f = File.OpenRead(path);
                bmp = Bitmap.DecodeToWidth(f, width);
            }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
            {
                // ponytail: a picture that cannot be read shows as empty
            }
            finally
            {
                Gate.Release();
            }

            if (watch.ElapsedMilliseconds >= 100)
            {
                Log.Write("picture", $"slow read {Path.GetFileName(path)} ({width} px)", watch.Elapsed.TotalMilliseconds);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (bmp is not null && !Map.ContainsKey(key))
                {
                    Map[key] = Lru.AddFirst((key, bmp));
                    _bytes += Bytes(bmp);
                    if (_bytes > Budget)
                    {
                        Log.Write("picture", $"cache full ({_bytes >> 20} MB, {Lru.Count} pictures): dropping the oldest");
                    }

                    while (_bytes > Budget && Lru.Count > 1)
                    {
                        // ponytail: an evicted bitmap may still be on screen; it is not disposed, the GC frees it once unused
                        _bytes -= Bytes(Lru.Last!.Value.Bmp);
                        Map.Remove(Lru.Last.Value.Key);
                        Lru.RemoveLast();
                    }
                }

                done(bmp);
            });
        });
    }

    /// <summary>Shows the picture in <paramref name="img"/> only while it is on screen, so off-screen tiles hold no bitmap
    /// and the budget above really bounds memory.</summary>
    public static void Bind(Avalonia.Controls.Image img, string path, int width = Width)
    {
        img.AttachedToVisualTree += (_, _) => Get(path, b =>
        {
            if (img.IsAttachedToVisualTree())
            {
                img.Source = b;
            }
        }, width);
        img.DetachedFromVisualTree += (_, _) => img.Source = null;
    }

    private static long Bytes(Bitmap b) => (long)b.PixelSize.Width * b.PixelSize.Height * 4;
}
