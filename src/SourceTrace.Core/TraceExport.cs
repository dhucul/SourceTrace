using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;

namespace SourceTrace.Core
{
    public static class TraceExport
    {
        public static void WriteCsv(TextWriter writer, IEnumerable<TraceRecord> records)
            => WriteCsv(writer, new TraceExportSnapshot(records.ToArray(), int.MaxValue, 0, false));

        public static void WriteCsv(TextWriter writer, TraceExportSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            writer.WriteLine("Sequence,TimestampUtc,ElapsedMilliseconds,Operation,Reason,ProcessId,ThreadId,FrameIndex,Function,Module,File,Line,Source,SourceState,RecordLimit,DroppedStops,AtCapacity,RecordingInterrupted");
            foreach (var r in snapshot.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var s = r.Location;
                writer.WriteLine(string.Join(",", new[] {
                    Number(r.Sequence), Csv(r.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)), Number(r.ElapsedMilliseconds),
                    Csv(r.Operation), Csv(r.Reason), Number(s.ProcessId), Number(s.ThreadId), Number(s.FrameIndex),
                    Csv(s.Function), Csv(s.Module), Csv(s.File), Number(s.Line), Csv(s.Source), Csv(s.SourceState),
                    Number(snapshot.RecordLimit), Number(snapshot.DroppedStops), Boolean(snapshot.AtCapacity), Boolean(snapshot.RecordingInterrupted)
                }));
            }
        }

        public static void WriteJson(TextWriter writer, IEnumerable<TraceRecord> records)
            => WriteJson(writer, new TraceExportSnapshot(records.ToArray(), int.MaxValue, 0, false));

        public static void WriteJson(TextWriter writer, TraceExportSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            writer.WriteLine("{\"formatVersion\":2,\"kind\":\"debugger-source-stops\",\"recordLimit\":" + Number(snapshot.RecordLimit) +
                ",\"droppedStops\":" + Number(snapshot.DroppedStops) + ",\"atCapacity\":" + Boolean(snapshot.AtCapacity) +
                ",\"recordingInterrupted\":" + Boolean(snapshot.RecordingInterrupted) + ",\"records\":[");
            bool first = true;
            foreach (var r in snapshot.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!first) writer.WriteLine(",");
                first = false;
                var s = r.Location;
                writer.Write("{\"sequence\":" + Number(r.Sequence) + ",\"timestampUtc\":" + Json(r.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)) +
                    ",\"elapsedMilliseconds\":" + Number(r.ElapsedMilliseconds) + ",\"operation\":" + Json(r.Operation) + ",\"reason\":" + Json(r.Reason) +
                    ",\"processId\":" + Number(s.ProcessId) + ",\"threadId\":" + Number(s.ThreadId) + ",\"frameIndex\":" + Number(s.FrameIndex) +
                    ",\"function\":" + Json(s.Function) + ",\"module\":" + Json(s.Module) + ",\"file\":" + Json(s.File) +
                    ",\"line\":" + Number(s.Line) + ",\"source\":" + Json(s.Source) + ",\"sourceState\":" + Json(s.SourceState) + "}");
            }
            writer.WriteLine("\n]}");
        }

        private static string Number(object value) => Convert.ToString(value, CultureInfo.InvariantCulture);
        private static string Boolean(bool value) => value ? "true" : "false";
        private static string Csv(string value)
        {
            value = value ?? "";
            // Keep exported source text from being interpreted as spreadsheet formulas.
            if (value.Length > 0 && "=+-@\t\r\n".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        private static string Json(string value)
        {
            var b = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '"') b.Append("\\\"");
                else if (c == '\\') b.Append("\\\\");
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
    }
}
