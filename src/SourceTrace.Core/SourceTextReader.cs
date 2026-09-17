using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SourceTrace.Core
{
    // A bounded, dedicated background worker keeps slow network source paths off the IDE thread.
    public sealed class SourceTextReader : IDisposable
    {
        private readonly BlockingCollection<Request> queue;
        private readonly HashSet<Request> pending = new HashSet<Request>();
        private readonly object gate = new object();
        private readonly Func<string, int, string> read;
        private readonly Dictionary<string, CachedSource> cache = new Dictionary<string, CachedSource>(StringComparer.OrdinalIgnoreCase);
        private bool disposed;

        public SourceTextReader(int capacity = 256, Func<string, int, string> readLine = null)
        {
            queue = new BlockingCollection<Request>(capacity);
            read = readLine ?? ReadFileLine;
            new Thread(Work) { IsBackground = true, Name = "SourceTrace source reader" }.Start();
        }

        public Task<string> ReadAsync(string file, int line)
        {
            if (string.IsNullOrWhiteSpace(file) || line <= 0) return Task.FromResult<string>(null);
            var request = new Request { File = file, Line = line };
            lock (gate)
            {
                if (disposed) return Task.FromResult<string>(null);
                pending.Add(request);
                if (!queue.TryAdd(request)) { pending.Remove(request); request.Result.SetResult(null); }
            }
            return request.Result.Task;
        }

        private void Work()
        {
            try
            {
                foreach (var request in queue.GetConsumingEnumerable())
                {
                    if (!request.Result.Task.IsCompleted)
                    {
                        string text = null;
                        try { text = read(request.File, request.Line); }
                        catch (Exception) { /* A missing/unreadable snippet must never stop the debugger. */ }
                        request.Result.TrySetResult(text);
                    }
                    lock (gate) pending.Remove(request);
                }
            }
            finally { cache.Clear(); queue.Dispose(); }
        }

        private string ReadFileLine(string file, int line)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 2 * 1024 * 1024) return null;
            if (!cache.TryGetValue(file, out var entry) || entry.Modified != info.LastWriteTimeUtc)
            {
                if (cache.Count >= 16) cache.Clear();
                entry = new CachedSource { Modified = info.LastWriteTimeUtc, Lines = File.ReadAllLines(file) };
                cache[file] = entry;
            }
            if (line > entry.Lines.Length) return null;
            var value = entry.Lines[line - 1].Trim();
            return value.Length > 2048 ? value.Substring(0, 2048) + "…" : value;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (var request in pending) request.Result.TrySetCanceled();
                pending.Clear();
                queue.CompleteAdding();
            }
            // Never join a worker that might be waiting on filesystem/network I/O.
        }

        private sealed class CachedSource { public DateTime Modified; public string[] Lines; }
        private sealed class Request
        {
            public string File;
            public int Line;
            public readonly TaskCompletionSource<string> Result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
