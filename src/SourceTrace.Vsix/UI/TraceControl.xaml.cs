using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using SourceTrace.Core;

namespace SourceTrace.UI
{
    public partial class TraceControl : UserControl
    {
        private const int VisibleLimit = SourceFollower.VisibleRecordLimit;
        private SourceTracePackage package;
        private TraceController controller;
        private readonly ObservableCollection<TraceRecord> visible = new ObservableCollection<TraceRecord>();
        private ICollectionView view;
        private DispatcherTimer refresh;
        private TraceRecord anchor;
        private int seen;
        private bool dirty = true;
        private bool exporting;
        private long sourceRevision;
        private string lastRefreshError;
        private bool updatingSelection;

        public TraceControl() { InitializeComponent(); }

        internal void Initialize(SourceTracePackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.package = package;
            controller = package.Controller;
            updatingSelection = true;
            try { FollowLatest.IsChecked = package.SourceSelection.Following; FilterBox.Text = package.SourceSelection.Filter; }
            finally { updatingSelection = false; }
            view = CollectionViewSource.GetDefaultView(visible);
            view.Filter = MatchesFilter;
            TraceGrid.ItemsSource = view;
            refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            refresh.Tick += Refresh;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            if (IsLoaded) OnLoaded(this, new RoutedEventArgs());
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            controller.Changed -= TraceChanged;
            controller.Changed += TraceChanged;
            package.ActivityStatus.Changed -= TraceChanged;
            package.ActivityStatus.Changed += TraceChanged;
            package.SourceSelection.Changed -= TraceChanged;
            package.SourceSelection.Changed += TraceChanged;
            dirty = true;
            refresh.Start();
            Refresh(null, EventArgs.Empty);
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            refresh.Stop(); controller.Changed -= TraceChanged; package.ActivityStatus.Changed -= TraceChanged;
            package.SourceSelection.Changed -= TraceChanged;
        }
        private void TraceChanged(object sender, EventArgs e)
        {
            dirty = true;
            bool previous = updatingSelection;
            updatingSelection = true;
            try
            {
                // Clear rows immediately. Membership checks also reject already queued input from old rows.
                if (controller.Records.Count == 0) { visible.Clear(); seen = 0; anchor = null; }
                SyncSelectedRow(false);
            }
            catch (Exception ex) { SourceTracePackage.LogError(ex); }
            finally { updatingSelection = previous; }
        }

        private void Refresh(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (controller.IsDisposed) { refresh.Stop(); return; }
            if (!dirty) return;
            dirty = false;
            bool wasUpdatingSelection = updatingSelection;
            updatingSelection = true;
            try { RefreshView(); lastRefreshError = null; }
            catch (Exception ex)
            {
                dirty = true;
                IntoButton.IsEnabled = OverButton.IsEnabled = OutButton.IsEnabled = ContinueButton.IsEnabled = false;
                if (lastRefreshError != ex.Message) SourceTracePackage.LogError(ex);
                lastRefreshError = ex.Message;
            }
            finally { updatingSelection = wasUpdatingSelection; }
        }

        private void RefreshView()
        {
            var all = controller.Records;
            if (all.Count < seen || (seen > 0 && all.Count > 0 && !ReferenceEquals(anchor, all[0])))
            { visible.Clear(); seen = 0; anchor = null; }
            if (all.Count > 0) anchor = all[0];
            for (int i = Math.Max(seen, all.Count - VisibleLimit); i < all.Count; i++) visible.Add(all[i]);
            while (visible.Count > VisibleLimit) visible.RemoveAt(0);
            seen = all.Count;
            if (sourceRevision != controller.SourceRevision) { view.Refresh(); sourceRevision = controller.SourceRevision; }
            EmptyState.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountLabel.Text = all.Count.ToString("N0") + " stops" + (all.Count > VisibleLimit ? " · latest 5,000 shown" : "");
            StatusLabel.Text = controller.Status;
            RecordingNotice.Text = controller.RecordingNotice;
            RecordingNotice.Visibility = string.IsNullOrEmpty(controller.RecordingNotice) ? Visibility.Collapsed : Visibility.Visible;
            ActivityLabel.Text = package.ActivityStatus.Message;
            bool ready = !controller.IsAutomatic && !controller.IsStepOutstanding && (controller.Mode == DebugMode.Break || controller.Mode == DebugMode.Design);
            IntoButton.IsEnabled = ContinueButton.IsEnabled = ready;
            OverButton.IsEnabled = OutButton.IsEnabled = ready && controller.Mode == DebugMode.Break;
            PauseButton.IsEnabled = controller.IsAutomatic || controller.Mode == DebugMode.Running;
            ClearButton.IsEnabled = ready && all.Count > 0;
            ExportButton.IsEnabled = all.Count > 0 && !exporting;
            SyncSelectedRow(package.SourceSelection.Following);
        }

        private bool MatchesFilter(object item)
        {
            return SourceFollower.Matches((TraceRecord)item, package.SourceSelection.Filter);
        }
        private void FilterChanged(object sender, TextChangedEventArgs e)
        {
            if (package == null || updatingSelection) return;
            updatingSelection = true;
            try { package.SetSourceFilter(FilterBox.Text); view?.Refresh(); SyncSelectedRow(true); }
            finally { updatingSelection = false; }
            dirty = true;
        }
        private void TraceInto(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); package.Execute(() => controller.Step(TraceStep.Into)); }
        private void TraceOver(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); package.Execute(() => controller.Step(TraceStep.Over)); }
        private void TraceOut(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); package.Execute(() => controller.Step(TraceStep.Out)); }
        private void ContinueTrace(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); package.Execute(() => controller.ContinueTrace()); }
        private void PauseTrace(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); package.PauseTrace(); }
        private void ClearTrace(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); package.Execute(() => controller.Clear()); }
        private void OpenSettings(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); package.ShowOptions(); }

        private void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedRecord();
            if (!updatingSelection && package != null && TraceGrid.SelectedItem is TraceRecord record)
            {
                package.SelectSource(record);
                SyncSelectedRow(false);
            }
        }

        private void FollowLatestChanged(object sender, RoutedEventArgs e)
        {
            if (package == null || updatingSelection) return;
            bool enabled = FollowLatest.IsChecked == true;
            package.SetFollowSource(enabled);
            SyncSelectedRow(enabled);
        }

        private void SyncSelectedRow(bool scroll)
        {
            if (view == null) return;
            bool wasUpdatingSelection = updatingSelection;
            updatingSelection = true;
            try
            {
                var selected = package.SourceSelection.SelectedRecord;
                FollowLatest.IsChecked = package.SourceSelection.Following;
                TraceGrid.SelectedItem = selected != null && view.Contains(selected) ? selected : null;
                if (scroll && TraceGrid.SelectedItem != null) TraceGrid.ScrollIntoView(TraceGrid.SelectedItem);
                UpdateSelectedRecord();
            }
            finally { updatingSelection = wasUpdatingSelection; }
        }

        private void UpdateSelectedRecord()
        {
            var row = package?.SourceSelection.SelectedRecord;
            if (row == null)
            {
                StatementBox.Text = "";
                LocationLabel.Text = "Double-click a row to open its source location.";
                LocationLabel.ToolTip = null;
                return;
            }
            LocationLabel.Text = row.Location.HasSource ? row.Location.File + ":" + row.Location.Line : "Source unavailable for this stop.";
            LocationLabel.ToolTip = LocationLabel.Text;
            StatementBox.Text = row.Location.SourceState == "Pending" ? "Loading source text..." : row.Location.Source;
        }
        private void OpenSelectedSource(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ItemsControl.ContainerFromElement(TraceGrid, e.OriginalSource as DependencyObject) is DataGridRow && TraceGrid.SelectedItem is TraceRecord record)
            { package.OpenSource(record); SyncSelectedRow(false); }
        }
        private void GridKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (e.Key == Key.Enter && TraceGrid.SelectedItem is TraceRecord record) { package.OpenSource(record); SyncSelectedRow(false); e.Handled = true; }
        }

        private void ExportTrace(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            package.JoinableTaskFactory.RunAsync(ExportTraceAsync).FileAndForget("SourceTrace/Export");
        }

        private async Task ExportTraceAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (exporting || controller.IsDisposed) return;
            exporting = true;
            ExportButton.IsEnabled = false;
            long ticket = package.ActivityStatus.Begin("Export: choose a destination.");
            try
            {
                // Snapshot on the UI thread; write outside the debugger thread.
                var dialog = new SaveFileDialog
                {
                    Title = "Export source trace", Filter = "JSON trace (*.json)|*.json|CSV spreadsheet (*.csv)|*.csv",
                    FileName = "source-trace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), AddExtension = true, DefaultExt = ".json"
                };
                if (dialog.ShowDialog() != true) { package.ActivityStatus.Complete(ticket, "Export canceled."); return; }
                var snapshot = controller.CreateExportSnapshot();
                bool csv = Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase);
                var cancellation = package.ShutdownToken;
                package.ActivityStatus.Complete(ticket, "Export: writing " + snapshot.Records.Count.ToString("N0") + " stops...");
                await Task.Run(() =>
                {
                    AtomicTraceFile.Write(dialog.FileName, csv, writer =>
                    {
                        if (csv) TraceExport.WriteCsv(writer, snapshot, cancellation);
                        else TraceExport.WriteJson(writer, snapshot, cancellation);
                    }, cancellation);
                }, cancellation);
                if (!controller.IsDisposed)
                    package.ActivityStatus.Complete(ticket, "Exported " + snapshot.Records.Count.ToString("N0") + " stops to " + dialog.FileName);
            }
            catch (OperationCanceledException) { if (!controller.IsDisposed) package.ActivityStatus.Complete(ticket, "Export canceled."); }
            catch (Exception ex)
            {
                SourceTracePackage.LogError(ex);
                if (!controller.IsDisposed) package.ActivityStatus.Complete(ticket, "Export failed: " + ex.Message);
            }
            finally { exporting = false; if (!controller.IsDisposed) { ExportButton.IsEnabled = controller.Records.Count > 0; dirty = true; } }
        }
    }
}
