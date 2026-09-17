using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SourceTrace.Editor;
using Microsoft.VisualStudio.TextManager.Interop;

internal static class Program
{
    private static int passed, failed;
    [STAThread]
    private static int Main()
    {
        Test("A matching source line produces a marker, and Clear removes it", () =>
        {
            using var editor = new EditorFixture(); using var tagger = editor.CreateTagger();
            SourceHighlight.Set("a.cs", 2); var tags = tagger.GetTags(editor.Visible()).ToArray();
            Check(tags.Length == 1 && tags[0].Span.Start.Position == 10 && tags[0].Span.Length == 9);
            SourceHighlight.Set(null, 0); Check(!tagger.GetTags(editor.Visible()).Any());
        });
        Test("Rename invalidates the old document's tracking span", () =>
        {
            using var editor = new EditorFixture(); using var tagger = editor.CreateTagger();
            SourceHighlight.Set("a.cs", 2); Check(tagger.GetTags(editor.Visible()).Any());
            editor.Rename("renamed.cs"); Check(!tagger.GetTags(editor.Visible()).Any());
            SourceHighlight.Set("renamed.cs", 3); Check(tagger.GetTags(editor.Visible()).Single().Span.Start.Position == 20);
        });
        Test("Tag retrieval rejects a changed path even before the rename notification", () =>
        {
            using var editor = new EditorFixture(); using var tagger = editor.CreateTagger();
            SourceHighlight.Set("a.cs", 1); editor.Path = "renamed.cs";
            Check(!tagger.GetTags(editor.Visible()).Any());
        });
        Test("Switching the highlight clears markers in other open files", () =>
        {
            using var a = new EditorFixture(); using var b = new EditorFixture { Path = "b.cs" };
            using var tagA = a.CreateTagger(); using var tagB = b.CreateTagger();
            SourceHighlight.Set("a.cs", 1); Check(tagA.GetTags(a.Visible()).Any()); Check(!tagB.GetTags(b.Visible()).Any());
            SourceHighlight.Set("b.cs", 1); Check(!tagA.GetTags(a.Visible()).Any()); Check(tagB.GetTags(b.Visible()).Any());
        });
        Test("Out-of-range lines produce no invalid spans", () =>
        {
            using var editor = new EditorFixture(); using var tagger = editor.CreateTagger();
            SourceHighlight.Set("a.cs", 200); Check(!tagger.GetTags(editor.Visible()).Any());
            SourceHighlight.Set("a.cs", -1); Check(!tagger.GetTags(editor.Visible()).Any());
        });
        Test("Closing a view removes document and buffer subscriptions", () =>
        {
            using var editor = new EditorFixture(); var tagger = editor.CreateTagger();
            Check(editor.DocumentProxy.Count("FileActionOccurred") == 1);
            editor.ViewProxy.Raise("Closed", editor.View, EventArgs.Empty);
            Check(editor.DocumentProxy.Count("FileActionOccurred") == 0 && editor.BufferProxy.Count("Changed") == 0);
            SourceHighlight.Set("a.cs", 1); Check(!tagger.GetTags(editor.Visible()).Any());
        });
        Test("A failing view notification does not stop other views being invalidated", () =>
        {
            using var a = new EditorFixture(); using var b = new EditorFixture { Path = "b.cs" };
            using var tagA = a.CreateTagger(); using var tagB = b.CreateTagger(); int notifications = 0;
            tagA.TagsChanged += (_, _) => throw new InvalidOperationException("closing view");
            tagB.TagsChanged += (_, _) => notifications++;
            SourceHighlight.Set("b.cs", 2); Check(notifications == 1 && tagB.GetTags(b.Visible()).Any());
        });
        Test("File-backed subject buffers are supported in projection views", () =>
        {
            using var editor = new EditorFixture(); editor.ViewBuffer = Proxy<ITextBuffer>.Create((m, a) => null).Object;
            var factory = Proxy<ITextDocumentFactoryService>.Create((method, args) =>
            {
                if (method.Name != "TryGetTextDocument") throw new NotSupportedException(method.Name);
                args[1] = editor.Document; return ReferenceEquals(args[0], editor.Buffer);
            });
            var provider = new TraceLineTaggerProvider { Documents = factory.Object };
            using var tagger = (TraceLineTagger)provider.CreateTagger<TextMarkerTag>(editor.View, editor.Buffer);
            Check(tagger != null); SourceHighlight.Set("a.cs", 2); Check(tagger.GetTags(editor.Visible()).Any());
        });
        Test("Unsupported tag types do not create orphan subscriptions", () =>
        {
            using var editor = new EditorFixture();
            var provider = new TraceLineTaggerProvider();
            Check(provider.CreateTagger<OtherTag>(editor.View, editor.Buffer) == null);
            Check(editor.ViewProxy.Count("Closed") == 0 && editor.DocumentProxy.Count("FileActionOccurred") == 0);
        });
        Test("High contrast can be changed live and restores the original custom marker style", () =>
        {
            var map = new FormatFixture();
            using var theme = (TraceMarkerTheme)Activator.CreateInstance(typeof(TraceMarkerTheme), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { map.Proxy.Object, Dispatcher.CurrentDispatcher }, null);
            theme.Apply(true, Brushes.White); Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.FillId], Brushes.Transparent));
            theme.Apply(true, Brushes.Yellow);
            Check(ReferenceEquals(((Pen)map.Properties[MarkerFormatDefinition.BorderId]).Brush, Brushes.Yellow));
            theme.Apply(false, Brushes.White);
            Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.FillId], map.OriginalFill));
            Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.BorderId], map.OriginalBorder));
            Check((string)map.Properties["unrelated"] == "preserved");
        });
        Test("Shared format maps keep one high-contrast baseline until their last view closes", () =>
        {
            var map = new FormatFixture();
            var service = Proxy<IEditorFormatMapService>.Create((m, a) => map.Proxy.Object);
            var first = WpfView(); var second = WpfView();
            TraceMarkerTheme.Attach(first.Object, service.Object); TraceMarkerTheme.Attach(first.Object, service.Object);
            TraceMarkerTheme.Attach(second.Object, service.Object);
            var registry = (IDictionary)typeof(TraceMarkerTheme).GetField("maps", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var theme = (TraceMarkerTheme)registry[map.Proxy.Object]; theme.Apply(true, Brushes.White);
            first.Raise("Closed", first.Object, EventArgs.Empty);
            Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.FillId], Brushes.Transparent));
            second.Raise("Closed", second.Object, EventArgs.Empty);
            Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.FillId], map.OriginalFill));
            Check(!registry.Contains(map.Proxy.Object));
        });
        Test("An external style update during high contrast is preserved when normal colors return", () =>
        {
            var map = new FormatFixture(); bool contrast = true;
            using var theme = new TraceMarkerTheme(map.Proxy.Object, Dispatcher.CurrentDispatcher, () => contrast, () => Brushes.White);
            var custom = new ResourceDictionary();
            foreach (DictionaryEntry entry in map.Properties) custom[entry.Key] = entry.Value;
            custom[MarkerFormatDefinition.FillId] = Brushes.Orange;
            map.Proxy.Object.SetProperties(SourceHighlight.FormatName, custom);
            Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.FillId], Brushes.Transparent));
            contrast = false; theme.Apply(false, Brushes.White);
            Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.FillId], Brushes.Orange));
        });
        Test("Native source marker uses an always-visible result location and clears only its own marker", () =>
        {
            using var editor = new EditorFixture(); var native = new NativeFixture(); int managedClears = 0;
            using var highlighter = new NativeLineHighlight(() => managedClears++);
            highlighter.Show(native.Buffer.Object, editor.Document, 3, 20, () => true);
            Check(highlighter.HasMarker && native.Type == (int)MARKERTYPE.MARKER_LIST_LOCATION);
            Check((native.Style & (uint)MARKERVISUAL.MV_COLOR_ALWAYS) != 0);
            Check(highlighter.Span().Value.iStartLine == 3);
            highlighter.Clear(); Check(!highlighter.HasMarker && native.Invalidations == 1 && managedClears == 1);
        });
        Test("Renaming a document removes its native marker", () =>
        {
            using var editor = new EditorFixture(); var native = new NativeFixture();
            using var highlighter = new NativeLineHighlight(() => { });
            highlighter.Show(native.Buffer.Object, editor.Document, 0, 9, () => true);
            editor.Rename("renamed.cs"); Check(!highlighter.HasMarker && native.Invalidations == 1);
        });
        Test("A canceled native marker request invalidates its temporary marker", () =>
        {
            var native = new NativeFixture(); bool current = true; native.AfterCreate = () => current = false;
            using var highlighter = new NativeLineHighlight(() => { });
            highlighter.Show(native.Buffer.Object, null, 0, 9, () => current);
            Check(!highlighter.HasMarker && native.Invalidations == 1);
        });
        Test("An old rename notification cannot remove a newer native marker", () =>
        {
            using var editor = new EditorFixture(); var first = new NativeFixture(); var second = new NativeFixture();
            using var highlighter = new NativeLineHighlight(() => { });
            highlighter.Show(first.Buffer.Object, editor.Document, 0, 9, () => true);
            var oldNotification = editor.DocumentProxy.Snapshot("FileActionOccurred");
            editor.Path = "renamed.cs";
            highlighter.Show(second.Buffer.Object, editor.Document, 1, 9, () => true);
            oldNotification.DynamicInvoke(editor.Document, new TextDocumentFileActionEventArgs(editor.Path, DateTime.UtcNow, FileActionTypes.DocumentRenamed));
            Check(highlighter.HasMarker && second.Invalidations == 0);
        });
        Test("Advancing the current instruction retains earlier visited highlights", () =>
        {
            using var editor = new EditorFixture(); using var trail = editor.CreateTrail(); using var current = editor.CreateTagger();
            SourceTrail.Add("a.cs", 1); SourceHighlight.Set("a.cs", 1);
            SourceTrail.Add("a.cs", 2); SourceHighlight.Set("a.cs", 2);
            Check(trail.GetTags(editor.Visible()).Single().Span.Start.Position == 0);
            Check(current.GetTags(editor.Visible()).Single().Span.Start.Position == 10);
            SourceHighlight.Set(null, 0); Check(trail.GetTags(editor.Visible()).Count() == 2);
        });
        Test("Trail survives file switches and reopening an editor", () =>
        {
            SourceTrail.Add("a.cs", 1); SourceTrail.Add("b.cs", 3);
            using var a = new EditorFixture(); using var b = new EditorFixture { Path = "b.cs" };
            using var ta = a.CreateTrail(); using var tb = b.CreateTrail();
            SourceHighlight.Set("b.cs", 3);
            Check(ta.GetTags(a.Visible()).Single().Span.Start.Position == 0);
            Check(!tb.GetTags(b.Visible()).Any());
            ta.Dispose(); using var reopened = a.CreateTrail();
            Check(reopened.GetTags(a.Visible()).Count() == 1 && SourceTrail.Count == 2);
        });
        Test("Loop revisits are deduplicated and invalid locations are ignored", () =>
        {
            using var editor = new EditorFixture(); using var trail = editor.CreateTrail();
            SourceTrail.Add("a.cs", 1); SourceTrail.Add("A.CS", 1);
            SourceTrail.Add(null, 1); SourceTrail.Add("a.cs", 0); SourceTrail.Add("a.cs", 500);
            Check(SourceTrail.Count == 2 && trail.GetTags(editor.Visible()).Count() == 1);
        });
        Test("Clear removes trail highlights from every open file", () =>
        {
            using var a = new EditorFixture(); using var b = new EditorFixture { Path = "b.cs" };
            using var ta = a.CreateTrail(); using var tb = b.CreateTrail();
            SourceTrail.Add("a.cs", 1); SourceTrail.Add("b.cs", 2); SourceTrail.Clear();
            Check(SourceTrail.Count == 0 && !ta.GetTags(a.Visible()).Any() && !tb.GetTags(b.Visible()).Any());
        });
        Test("Trail markers reject a renamed document before and after notification", () =>
        {
            using var editor = new EditorFixture(); using var trail = editor.CreateTrail();
            SourceTrail.Add("a.cs", 1); editor.Path = "b.cs";
            Check(!trail.GetTags(editor.Visible()).Any());
            editor.Rename("b.cs"); SourceTrail.Add("b.cs", 2);
            Check(trail.GetTags(editor.Visible()).Single().Span.Start.Position == 10);
        });
        Test("Disposed trail views release subscriptions and do not remove other views' history", () =>
        {
            using var editor = new EditorFixture(); var trail = editor.CreateTrail();
            SourceTrail.Add("a.cs", 1); trail.Dispose();
            Check(editor.DocumentProxy.Count("FileActionOccurred") == 0 && editor.BufferProxy.Count("Changed") == 0);
            Check(!trail.GetTags(editor.Visible()).Any() && SourceTrail.Count == 1);
        });
        Test("Reentrant Clear during an addition cannot restore an old trail", () =>
        {
            EventHandler<TrailChange> clear = (_, change) => { if (change.File != null) SourceTrail.Clear(); };
            SourceTrail.Changed += clear;
            try
            {
                using var editor = new EditorFixture(); using var trail = editor.CreateTrail();
                SourceTrail.Add("a.cs", 1);
                Check(SourceTrail.Count == 0 && !trail.GetTags(editor.Visible()).Any());
            }
            finally { SourceTrail.Changed -= clear; }
        });
        Test("A new visit reentered during Clear is retained in every view", () =>
        {
            SourceTrail.Add("a.cs", 1);
            EventHandler<TrailChange> add = (_, change) => { if (change.File == null) SourceTrail.Add("a.cs", 2); };
            SourceTrail.Changed += add;
            try
            {
                using var editor = new EditorFixture(); using var trail = editor.CreateTrail();
                SourceTrail.Clear();
                Check(SourceTrail.Count == 1 && trail.GetTags(editor.Visible()).Single().Span.Start.Position == 10);
            }
            finally { SourceTrail.Changed -= add; }
        });
        Test("Clear during tracking-span creation cannot publish a stale trail marker", () =>
        {
            using var editor = new EditorFixture(); using var trail = editor.CreateTrail();
            editor.BeforeTrackingSpan = () => { editor.BeforeTrackingSpan = null; SourceTrail.Clear(); SourceTrail.Add("a.cs", 2); };
            SourceTrail.Add("a.cs", 1);
            Check(trail.GetTags(editor.Visible()).Single().Span.Start.Position == 10);
        });
        Test("Visited-line theme uses its own format and restores custom colors", () =>
        {
            var map = new FormatFixture();
            using var theme = new TraceMarkerTheme(map.Proxy.Object, Dispatcher.CurrentDispatcher, () => false, () => Brushes.White, SourceTrail.FormatName);
            theme.Apply(true, Brushes.White); Check(map.LastName == SourceTrail.FormatName);
            theme.Apply(false, Brushes.White); Check(ReferenceEquals(map.Properties[MarkerFormatDefinition.FillId], map.OriginalFill));
        });
        Test("Reentrant marker selection during span creation preserves the latest target", () =>
        {
            using var editor = new EditorFixture(); using var tagger = editor.CreateTagger();
            editor.BeforeTrackingSpan = () =>
            {
                editor.BeforeTrackingSpan = null;
                SourceHighlight.Set("a.cs", 3);
            };
            SourceHighlight.Set("a.cs", 2);
            Check(SourceHighlight.Current.Line == 3);
            var tags = tagger.GetTags(editor.Visible()).ToArray();
            Check(tags.Length == 1 && tags[0].Span.Start.Position == 20);
        });

        Console.WriteLine($"\n{passed} editor tests passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
    private static Proxy<IWpfTextView> WpfView()
    {
        var element = new Grid(); var properties = new PropertyCollection();
        return Proxy<IWpfTextView>.Create((method, args) => method.Name switch
        { "get_VisualElement" => element, "get_IsClosed" => false, "get_Properties" => properties, _ => throw new NotSupportedException(method.Name) });
    }
    private static void Test(string name, Action body)
    {
        try { SourceHighlight.Set(null, 0); SourceTrail.Clear(); body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void Check(bool result) { if (!result) throw new Exception("Assertion failed."); }
    private sealed class OtherTag : TextMarkerTag { public OtherTag() : base("Other") { } }

    private sealed class NativeFixture
    {
        public readonly Proxy<IVsTextLines> Buffer;
        public readonly Proxy<IVsTextLineMarker> Marker;
        public TextSpan Span;
        public uint Style;
        public int Type, Invalidations;
        public Action AfterCreate;
        public NativeFixture()
        {
            Marker = Proxy<IVsTextLineMarker>.Create((method, args) =>
            {
                switch (method.Name)
                {
                    case "GetVisualStyle": args[0] = Style; return 0;
                    case "SetVisualStyle": Style = (uint)args[0]; return 0;
                    case "GetCurrentSpan": ((TextSpan[])args[0])[0] = Span; return 0;
                    case "Invalidate": Invalidations++; return 0;
                    default: throw new NotSupportedException(method.Name);
                }
            });
            Buffer = Proxy<IVsTextLines>.Create((method, args) =>
            {
                if (method.Name != "CreateLineMarker") throw new NotSupportedException(method.Name);
                Type = (int)args[0]; Span = new TextSpan { iStartLine = (int)args[1], iStartIndex = (int)args[2], iEndLine = (int)args[3], iEndIndex = (int)args[4] };
                ((IVsTextLineMarker[])args[6])[0] = Marker.Object; AfterCreate?.Invoke(); return 0;
            });
        }
    }

    private sealed class FormatFixture
    {
        public readonly Brush OriginalFill = Brushes.LightBlue;
        public readonly Pen OriginalBorder = new Pen(Brushes.DarkBlue, 1);
        public ResourceDictionary Properties;
        public string LastName;
        public readonly Proxy<IEditorFormatMap> Proxy;
        public FormatFixture()
        {
            Properties = new ResourceDictionary { [MarkerFormatDefinition.FillId] = OriginalFill,
                [MarkerFormatDefinition.BorderId] = OriginalBorder, ["unrelated"] = "preserved" };
            Proxy = Proxy<IEditorFormatMap>.Create((method, args) =>
            {
                if (method.Name == "GetProperties") { LastName = (string)args[0]; return Properties; }
                if (method.Name == "SetProperties")
                {
                    Properties = (ResourceDictionary)args[1];
                    Proxy.Raise("FormatMappingChanged", Proxy.Object, new FormatItemsEventArgs(Array.AsReadOnly(new[] { SourceHighlight.FormatName })));
                    return null;
                }
                throw new NotSupportedException(method.Name);
            });
        }
    }
    private sealed class EditorFixture : IDisposable
    {
        public string Path = "a.cs";
        public Action BeforeTrackingSpan;
        public readonly Proxy<ITextView> ViewProxy;
        public readonly Proxy<ITextBuffer> BufferProxy;
        public readonly Proxy<ITextDocument> DocumentProxy;
        public readonly ITextSnapshot Snapshot;
        public ITextBuffer ViewBuffer;
        public ITextView View => ViewProxy.Object;
        public ITextBuffer Buffer => BufferProxy.Object;
        public ITextDocument Document => DocumentProxy.Object;
        public EditorFixture()
        {
            BufferProxy = Proxy<ITextBuffer>.Create((method, args) => method.Name == "get_CurrentSnapshot" ? Snapshot : throw new NotSupportedException(method.Name));
            ViewBuffer = Buffer;
            var snapshot = Proxy<ITextSnapshot>.Create((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_Length": return 200;
                    case "get_LineCount": return 20;
                    case "get_TextBuffer": return Buffer;
                    case "GetLineFromLineNumber":
                        int start = (int)args[0] * 10;
                        return Proxy<ITextSnapshotLine>.Create((m, a) => m.Name switch
                        {
                            "get_Length" => 9,
                            "get_Extent" => new SnapshotSpan(Snapshot, start, 9),
                            "get_ExtentIncludingLineBreak" => new SnapshotSpan(Snapshot, start, 10),
                            _ => throw new NotSupportedException(m.Name)
                        }).Object;
                    case "CreateTrackingSpan":
                        BeforeTrackingSpan?.Invoke();
                        Span span = args[0] is SnapshotSpan snapshotSpan ? snapshotSpan.Span : (Span)args[0];
                        return Proxy<ITrackingSpan>.Create((m, a) => m.Name == "GetSpan" ? new SnapshotSpan((ITextSnapshot)a[0], span) : throw new NotSupportedException(m.Name)).Object;
                    default: throw new NotSupportedException(method.Name);
                }
            });
            Snapshot = snapshot.Object;
            ViewProxy = Proxy<ITextView>.Create((m, a) => m.Name == "get_TextBuffer" ? ViewBuffer : throw new NotSupportedException(m.Name));
            DocumentProxy = Proxy<ITextDocument>.Create((m, a) => m.Name == "get_FilePath" ? Path : throw new NotSupportedException(m.Name));
        }
        public TraceLineTagger CreateTagger() => new TraceLineTagger(View, Buffer, Document);
        public TrailLineTagger CreateTrail() => new TrailLineTagger(View, Buffer, Document);
        public NormalizedSnapshotSpanCollection Visible() => new NormalizedSnapshotSpanCollection(new SnapshotSpan(Snapshot, 0, 200));
        public void Rename(string path)
        {
            Path = path;
            DocumentProxy.Raise("FileActionOccurred", Document, new TextDocumentFileActionEventArgs(path, DateTime.UtcNow, FileActionTypes.DocumentRenamed));
        }
        public void Dispose() { ViewProxy.Raise("Closed", View, EventArgs.Empty); }
    }
}

public class EditorDispatchProxy : DispatchProxy
{
    public Func<MethodInfo, object[], object> Handler;
    public readonly Dictionary<string, Delegate> Events = new Dictionary<string, Delegate>();
    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (method.Name.StartsWith("add_", StringComparison.Ordinal))
        { string name = method.Name.Substring(4); Events.TryGetValue(name, out var handler); Events[name] = Delegate.Combine(handler, (Delegate)args[0]); return null; }
        if (method.Name.StartsWith("remove_", StringComparison.Ordinal))
        { string name = method.Name.Substring(7); Events.TryGetValue(name, out var handler); Events[name] = Delegate.Remove(handler, (Delegate)args[0]); return null; }
        return Handler(method, args);
    }
}
public sealed class Proxy<T> where T : class
{
    public T Object { get; private set; }
    private EditorDispatchProxy proxy;
    public static Proxy<T> Create(Func<MethodInfo, object[], object> handler)
    {
        var value = DispatchProxy.Create<T, EditorDispatchProxy>(); var proxy = (EditorDispatchProxy)(object)value; proxy.Handler = handler;
        return new Proxy<T> { Object = value, proxy = proxy };
    }
    public int Count(string name) => proxy.Events.TryGetValue(name, out var handler) ? handler?.GetInvocationList().Length ?? 0 : 0;
    public Delegate Snapshot(string name) => proxy.Events.TryGetValue(name, out var handler) ? handler : null;
    public void Raise(string name, params object[] args)
    {
        if (proxy.Events.TryGetValue(name, out var handler) && handler != null)
            foreach (var callback in handler.GetInvocationList()) callback.DynamicInvoke(args);
    }
}
