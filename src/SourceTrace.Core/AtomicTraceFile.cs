using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SourceTrace.Core
{
    public static class AtomicTraceFile
    {
        public static void Write(string destination, bool utf8Bom, Action<TextWriter> write, CancellationToken cancellationToken = default)
        {
            string target = Path.GetFullPath(destination);
            bool replaceExisting = File.Exists(target);
            string temporary = Path.Combine(Path.GetDirectoryName(target), ".sourcetrace-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(utf8Bom), 16384, true))
                    { write(writer); writer.Flush(); }
                    stream.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (replaceExisting) File.Replace(temporary, target, null);
                else File.Move(temporary, target); // Never overwrite a destination created after the existence check.
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
