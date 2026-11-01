using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NovReader.Services
{
    public sealed class ImageCacheStats
    {
        public int Count;
        public long Bytes;
    }

    /// <summary>漫画原始图片文件缓存：原子写入、不常驻图片字节、相同请求合并。</summary>
    public sealed class ComicImageCache
    {
        private const int MaxImageBytes = 32 * 1024 * 1024;
        private readonly Func<string> _folder;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Task<byte[]>> _pending = new Dictionary<string, Task<byte[]>>();
        private readonly SemaphoreSlim _storage = new SemaphoreSlim(1);
        private readonly SemaphoreSlim _downloads = new SemaphoreSlim(8);
        private int _epoch;
        public Action<string> Diagnostics;

        public ComicImageCache(Func<string> folder) { _folder = folder; }

        private static string Key(string url, string source)
        {
            using (var hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes((source ?? "") + "\0" + (url ?? "")));
                var key = new StringBuilder(64);
                foreach (byte b in bytes) key.Append(b.ToString("x2"));
                return key.ToString();
            }
        }

        public Task<byte[]> GetOrFetchAsync(string url, string source, Func<Task<byte[]>> fetch)
        {
            if (String.IsNullOrEmpty(url)) return Task.FromResult<byte[]>(null);
            string key = Key(url, source);
            TaskCompletionSource<byte[]> completion;
            int epoch;
            lock (_gate)
            {
                Task<byte[]> existing;
                if (_pending.TryGetValue(key, out existing)) return existing;
                completion = new TaskCompletionSource<byte[]>();
                _pending.Add(key, completion.Task); epoch = _epoch;
            }
            CompleteAsync(key, epoch, fetch, completion);
            return completion.Task;
        }

        private async void CompleteAsync(string key, int epoch, Func<Task<byte[]>> fetch, TaskCompletionSource<byte[]> completion)
        {
            byte[] bytes = null;
            try
            {
                bytes = await ReadAsync(key).ConfigureAwait(false);
                if (bytes != null) Report("comic image cache hit: " + key + "; bytes=" + bytes.Length);
                else
                {
                    await _downloads.WaitAsync().ConfigureAwait(false);
                    try { bytes = await fetch().ConfigureAwait(false); }
                    finally { _downloads.Release(); }
                    if (Cacheable(bytes)) await SaveAsync(key, bytes, epoch).ConfigureAwait(false);
                }
            }
            catch (Exception error) { Report("comic image cache/fetch failed: " + error.Message); }
            finally
            {
                lock (_gate)
                {
                    Task<byte[]> current;
                    if (_pending.TryGetValue(key, out current) && Object.ReferenceEquals(current, completion.Task)) _pending.Remove(key);
                }
                completion.TrySetResult(bytes);
            }
        }

        private async Task<byte[]> ReadAsync(string key)
        {
            try
            {
                using (var file = new FileStream(Path.Combine(_folder(), key + ".img"), FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true))
                {
                    if (file.Length < 12 || file.Length > MaxImageBytes) return null;
                    var bytes = new byte[(int)file.Length]; int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = await file.ReadAsync(bytes, offset, bytes.Length - offset).ConfigureAwait(false);
                        if (read == 0) return null;
                        offset += read;
                    }
                    return Cacheable(bytes) ? bytes : null;
                }
            }
            catch (Exception) { return null; }
        }

        private async Task SaveAsync(string key, byte[] bytes, int epoch)
        {
            await _storage.WaitAsync().ConfigureAwait(false);
            string temporary = null;
            try
            {
                lock (_gate) { if (epoch != _epoch) return; }
                string folder = _folder(); Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, key + ".img");
                temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await file.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    await file.FlushAsync().ConfigureAwait(false);
                }
                // ClearAsync is serialized with this write; an incomplete .part file is never a cache hit.
                if (File.Exists(target)) File.Delete(target);
                File.Move(temporary, target); temporary = null;
                Report("comic image cache saved: " + key + "; bytes=" + bytes.Length);
            }
            catch (Exception error) { Report("comic image cache write failed: " + error.Message); }
            finally
            {
                if (temporary != null) try { File.Delete(temporary); } catch (Exception) { }
                _storage.Release();
            }
        }

        public async Task<bool> ContainsAsync(string url, string source)
        {
            // Check file contents as well as existence, so a corrupt file is not "downloaded".
            return await ReadAsync(Key(url, source)).ConfigureAwait(false) != null;
        }

        public async Task RemoveAsync(string url, string source)
        {
            await _storage.WaitAsync().ConfigureAwait(false);
            try { File.Delete(Path.Combine(_folder(), Key(url, source) + ".img")); }
            catch (Exception) { }
            finally { _storage.Release(); }
        }

        public async Task ClearAsync()
        {
            lock (_gate) { _epoch++; _pending.Clear(); }
            await _storage.WaitAsync().ConfigureAwait(false);
            try
            {
                string folder = _folder(); if (!Directory.Exists(folder)) return;
                foreach (string file in Directory.GetFiles(folder))
                    if (file.EndsWith(".img", StringComparison.Ordinal) || file.EndsWith(".part", StringComparison.Ordinal)) File.Delete(file);
            }
            finally { _storage.Release(); }
        }

        public Task<ImageCacheStats> StatsAsync()
        {
            return Task.Run(delegate
            {
                var result = new ImageCacheStats();
                try
                {
                    string folder = _folder(); if (!Directory.Exists(folder)) return result;
                    foreach (string path in Directory.GetFiles(folder, "*.img"))
                    {
                        var file = new FileInfo(path); result.Count++; result.Bytes += file.Length;
                    }
                }
                catch (Exception) { }
                return result;
            });
        }

        private static bool Cacheable(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12 || bytes.Length > MaxImageBytes) return false;
            return (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) ||
                (bytes[0] == 137 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G' && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10) ||
                (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8' && (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a') ||
                (bytes[0] == 'B' && bytes[1] == 'M') ||
                (bytes[0] == 'I' && bytes[1] == 'I' && bytes[2] == 42 && bytes[3] == 0) ||
                (bytes[0] == 'M' && bytes[1] == 'M' && bytes[2] == 0 && bytes[3] == 42);
        }

        private void Report(string text) { try { if (Diagnostics != null) Diagnostics(text); } catch (Exception) { } }
    }
}
