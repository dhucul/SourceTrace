using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace SourceTrace.Editor
{
    // Format maps can be shared by several views. Keep one override/baseline per map.
    internal sealed class TraceMarkerTheme : IDisposable
    {
        private static readonly Dictionary<IEditorFormatMap, TraceMarkerTheme> maps = new Dictionary<IEditorFormatMap, TraceMarkerTheme>();
        private static readonly Lazy<JoinableTaskFactory> ui = new Lazy<JoinableTaskFactory>(() =>
            ThreadHelper.JoinableTaskContext.CreateFactory(ThreadHelper.JoinableTaskContext.CreateCollection()), LazyThreadSafetyMode.ExecutionAndPublication);
        private static JoinableTaskFactory UI => ui.Value;
        private readonly IEditorFormatMap map;
        private readonly string formatName;
        private readonly TraceMarkerTheme trailTheme;
        private readonly Dispatcher dispatcher;
        private readonly Func<bool> readHighContrast;
        private readonly Func<Brush> readHighlightBrush;
        private int views;
        private bool disposed, disposing, highContrast, applying;
        private object savedFill, savedBorder;
        private object appliedBorder;
        private bool hadFill, hadBorder;

        private TraceMarkerTheme(IEditorFormatMap map, Dispatcher dispatcher)
            : this(map, dispatcher, () => SystemParameters.HighContrast, () => SystemColors.HighlightBrush)
        {
            trailTheme = new TraceMarkerTheme(map, dispatcher, readHighContrast, readHighlightBrush, SourceTrail.FormatName);
        }

        internal TraceMarkerTheme(IEditorFormatMap map, Dispatcher dispatcher, Func<bool> readHighContrast, Func<Brush> readHighlightBrush)
            : this(map, dispatcher, readHighContrast, readHighlightBrush, SourceHighlight.FormatName) { }

        internal TraceMarkerTheme(IEditorFormatMap map, Dispatcher dispatcher, Func<bool> readHighContrast, Func<Brush> readHighlightBrush, string formatName)
        {
            this.map = map; this.dispatcher = dispatcher; this.formatName = formatName;
            this.readHighContrast = readHighContrast; this.readHighlightBrush = readHighlightBrush;
            SystemParameters.StaticPropertyChanged += SystemChanged;
            map.FormatMappingChanged += FormatChanged;
            ApplySystemSettings();
        }

        internal static void Attach(IWpfTextView view, IEditorFormatMapService service)
        {
            var dispatcher = view.VisualElement.Dispatcher;
            if (!dispatcher.CheckAccess())
            { UI.RunAsync(() => AttachAsync(view, service)).FileAndForget("SourceTrace/MarkerThemeAttach"); return; }
            if (view.IsClosed || service == null) return;
            try
            {
                view.Properties.GetOrCreateSingletonProperty(typeof(ViewLease), () =>
                {
                    var map = service.GetEditorFormatMap(view);
                    if (!maps.TryGetValue(map, out var theme))
                    { theme = new TraceMarkerTheme(map, dispatcher); maps.Add(map, theme); }
                    theme.views++;
                    return new ViewLease(view, theme);
                });
            }
            catch (Exception ex) { Trace.TraceError("SourceTrace marker theme: " + ex); }
        }

        private void SystemChanged(object sender, PropertyChangedEventArgs args)
        {
            if (disposed || disposing || dispatcher.HasShutdownStarted) return;
            if (dispatcher.CheckAccess()) ApplySystemSettings();
            else UI.RunAsync(ApplyAsync).FileAndForget("SourceTrace/MarkerThemeUpdate");
        }
        private static async Task AttachAsync(IWpfTextView view, IEditorFormatMapService service)
        { await UI.SwitchToMainThreadAsync(); Attach(view, service); }
        private async Task ApplyAsync()
        { await UI.SwitchToMainThreadAsync(); ApplySystemSettings(); }
        private void ApplySystemSettings()
        {
            if (disposed) return;
            try { Apply(readHighContrast(), readHighlightBrush()); }
            catch (Exception ex) { Trace.TraceError("SourceTrace marker theme: " + ex); }
        }

        private void FormatChanged(object sender, FormatItemsEventArgs args)
        {
            if (disposed || disposing || applying || !readHighContrast() || !args.ChangedItems.Contains(formatName)) return;
            try
            {
                var properties = map.GetProperties(formatName);
                // Preserve externally changed normal-style fields while retaining our accessibility override.
                if (!ReferenceEquals(properties[MarkerFormatDefinition.FillId], Brushes.Transparent))
                { hadFill = properties.Contains(MarkerFormatDefinition.FillId); savedFill = properties[MarkerFormatDefinition.FillId]; }
                if (!ReferenceEquals(properties[MarkerFormatDefinition.BorderId], appliedBorder))
                { hadBorder = properties.Contains(MarkerFormatDefinition.BorderId); savedBorder = properties[MarkerFormatDefinition.BorderId]; }
                ApplySystemSettings();
            }
            catch (Exception ex) { Trace.TraceError("SourceTrace marker format update: " + ex); }
        }

        internal void Apply(bool useHighContrast, Brush highlightBrush)
        {
            if (disposed || (!useHighContrast && !highContrast)) return;
            var properties = Copy(map.GetProperties(formatName));
            if (useHighContrast)
            {
                if (!highContrast)
                {
                    hadFill = properties.Contains(MarkerFormatDefinition.FillId);
                    hadBorder = properties.Contains(MarkerFormatDefinition.BorderId);
                    savedFill = properties[MarkerFormatDefinition.FillId];
                    savedBorder = properties[MarkerFormatDefinition.BorderId];
                }
                properties[MarkerFormatDefinition.FillId] = Brushes.Transparent;
                var border = new Pen(highlightBrush, 2);
                if (border.CanFreeze) border.Freeze();
                properties[MarkerFormatDefinition.BorderId] = border;
                appliedBorder = border;
            }
            else
            {
                if (hadFill) properties[MarkerFormatDefinition.FillId] = savedFill;
                else properties.Remove(MarkerFormatDefinition.FillId);
                if (hadBorder) properties[MarkerFormatDefinition.BorderId] = savedBorder;
                else properties.Remove(MarkerFormatDefinition.BorderId);
            }
            applying = true;
            try { map.SetProperties(formatName, properties); }
            finally { applying = false; }
            highContrast = useHighContrast;
        }
        private static ResourceDictionary Copy(ResourceDictionary source)
        {
            var result = new ResourceDictionary();
            foreach (DictionaryEntry entry in source) result.Add(entry.Key, entry.Value);
            return result;
        }
        private void ReleaseView()
        {
            if (--views != 0) return;
            maps.Remove(map);
            Dispose();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposing = true;
            trailTheme?.Dispose();
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            map.FormatMappingChanged -= FormatChanged;
            try { if (highContrast) Apply(false, Brushes.Transparent); }
            catch (Exception ex) { Trace.TraceError("SourceTrace marker theme cleanup: " + ex); }
            disposed = true;
        }
        private sealed class ViewLease
        {
            private readonly IWpfTextView view;
            private readonly TraceMarkerTheme theme;
            internal ViewLease(IWpfTextView view, TraceMarkerTheme theme)
            { this.view = view; this.theme = theme; view.Closed += Closed; }
            private void Closed(object sender, EventArgs args)
            { view.Closed -= Closed; theme.ReleaseView(); }
        }
    }
}
