#if SOURCETRACE_TEST_DIAGNOSTICS
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.TextManager.Interop;
using SourceTrace.Editor;
using SourceTrace.UI;

namespace SourceTrace
{
    [ProvideAutomationObject("SourceTrace.Test")]
    public sealed partial class SourceTracePackage
    {
        protected override object GetAutomationObject(string name)
        { return name == "SourceTrace.Test" ? new IntegrationProbe(this) : base.GetAutomationObject(name); }

        internal string DescribeEditor(string directory)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new StringBuilder();
            result.AppendLine("Following=" + SourceSelection.Following + "; selected=" + SourceSelection.SelectedRecord?.Location.File + ":" + SourceSelection.SelectedRecord?.Location.Line);
            result.AppendLine("Marker=" + SourceHighlight.Current?.File + ":" + SourceHighlight.Current?.Line);
            result.AppendLine("Trail unique lines=" + SourceTrail.Count);
            var nativeSpan = nativeHighlight?.Span();
            result.AppendLine("Native marker=" + nativeHighlight?.HasMarker + "; span=" + (nativeSpan.HasValue ? nativeSpan.Value.iStartLine + ":" + nativeSpan.Value.iStartIndex + "-" + nativeSpan.Value.iEndLine + ":" + nativeSpan.Value.iEndIndex : "none"));
            result.AppendLine("Taggers=" + SourceHighlight.TaggersCreated + "; GetTags=" + SourceHighlight.GetTagsCalls + "; TagsReturned=" + SourceHighlight.TagsReturned + "; lastDocument=" + SourceHighlight.LastDocument);
            var pane = FindToolWindow(typeof(TraceToolWindow), 0, false);
            result.AppendLine("Panel frame=" + (pane?.Frame is IVsWindowFrame frame ? frame.IsVisible().ToString() : "missing"));
            result.AppendLine("Panel requested=" + traceWindowRequested);
            foreach (EnvDTE.Window window in dte.Windows)
                if (window.Caption == "SourceTrace") result.AppendLine("Panel DTE.Visible=" + window.Visible + "; AutoHides=" + window.AutoHides + "; height=" + window.Height);
            if (pane?.Content is FrameworkElement panel)
            {
                result.AppendLine("Panel.IsVisible=" + panel.IsVisible + "; size=" + panel.ActualWidth + "x" + panel.ActualHeight);
                var grid = (System.Windows.Controls.DataGrid)panel.FindName("TraceGrid");
                result.AppendLine("Grid rows=" + grid.Items.Count + "; height=" + grid.ActualHeight);
                if (grid.SelectedItem != null && grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem) is FrameworkElement row)
                {
                    var origin = row.TranslatePoint(new Point(), panel);
                    var gridOrigin = grid.TranslatePoint(new Point(), panel);
                    var scroll = FindDescendant<System.Windows.Controls.ScrollViewer>(grid);
                    double scrollbar = scroll?.ComputedHorizontalScrollBarVisibility == Visibility.Visible ? SystemParameters.HorizontalScrollBarHeight : 0;
                    result.AppendLine("Selected row visible=" + (row.IsVisible && origin.Y >= gridOrigin.Y && origin.Y + row.ActualHeight <= gridOrigin.Y + grid.ActualHeight - scrollbar));
                }
                if (!string.IsNullOrEmpty(directory)) SaveVisual(panel, Path.Combine(directory, "panel.png"));
            }
            var component = (IComponentModel)GetService(typeof(SComponentModel)) ?? throw new InvalidOperationException("Component model unavailable.");
            var textManager = (IVsTextManager)GetService(typeof(SVsTextManager)) ?? throw new InvalidOperationException("Text manager unavailable.");
            if (textManager.GetActiveView(0, null, out var nativeView) >= 0 && nativeView != null)
            {
                var view = component.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(nativeView);
                result.AppendLine("WpfView=" + (view != null));
                if (view != null)
                {
                    result.AppendLine("View roles=" + string.Join(",", view.Roles));
                    result.AppendLine("Editor.IsVisible=" + view.VisualElement.IsVisible + "; size=" + view.VisualElement.ActualWidth + "x" + view.VisualElement.ActualHeight);
                    result.AppendLine("Editor.ViewportLeft=" + view.ViewportLeft);
                    result.AppendLine("Content type=" + view.TextBuffer.ContentType.TypeName);
                    var properties = component.GetService<IEditorFormatMapService>().GetEditorFormatMap(view).GetProperties(SourceHighlight.FormatName);
                    foreach (System.Collections.DictionaryEntry property in properties) result.AppendLine("Format " + property.Key + "=" + property.Value);
                    if (!string.IsNullOrEmpty(directory)) SaveVisual(view.VisualElement, Path.Combine(directory, "editor.png"));
                    using (var tags = component.GetService<IViewTagAggregatorFactoryService>().CreateTagAggregator<TextMarkerTag>(view))
                    {
                        var found = tags.GetTags(new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length)).Where(t => t.Tag.Type == SourceHighlight.FormatName).ToArray();
                        result.AppendLine("Queried marker spans=" + found.Length);
                        var trail = tags.GetTags(new SnapshotSpan(view.TextSnapshot, 0, view.TextSnapshot.Length)).Where(t => t.Tag.Type == SourceTrail.FormatName).ToArray();
                        result.AppendLine("Trail rendered lines=" + string.Join(",", trail.SelectMany(t => t.Span.GetSpans(view.TextSnapshot)).Select(s => s.Start.GetContainingLine().LineNumber + 1).Distinct().OrderBy(n => n)));
                    }
                }
            }
            result.AppendLine("After query: taggers=" + SourceHighlight.TaggersCreated + "; GetTags=" + SourceHighlight.GetTagsCalls + "; TagsReturned=" + SourceHighlight.TagsReturned);
            result.AppendLine("Activity=" + ActivityStatus.Message);
            return result.ToString();
        }
        private static T FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var descendant = FindDescendant<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }
        private static void SaveVisual(FrameworkElement element, string file)
        {
            if (!element.IsVisible || element.ActualWidth < 1 || element.ActualHeight < 1) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class IntegrationProbe
    {
        private readonly SourceTracePackage package;
        internal IntegrationProbe(SourceTracePackage package) { this.package = package; }
        public string Inspect(string directory) => package.JoinableTaskFactory.Run(async () =>
        { await package.JoinableTaskFactory.SwitchToMainThreadAsync(); return package.DescribeEditor(directory); });
        public void ClearTrace() => package.JoinableTaskFactory.Run(async () =>
        { await package.JoinableTaskFactory.SwitchToMainThreadAsync(); package.Controller.Clear(); });
        public void Filter(string text) => package.JoinableTaskFactory.Run(async () =>
        { await package.JoinableTaskFactory.SwitchToMainThreadAsync(); package.SetSourceFilter(text); });
    }
}
#endif
