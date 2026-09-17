using System.ComponentModel;
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SourceTrace.Core;

namespace SourceTrace
{
    [Guid("fa90488c-6423-3fb7-9b38-24dec836aa3e")]
    public sealed class TraceOptions : DialogPage
    {
        internal event EventHandler Applied;
        [Category("Continuous tracing"), DisplayName("Delay between steps (ms)"), Description("Delay between automatic source steps, from 1 to 5000 milliseconds.")]
        public int DelayMilliseconds { get; set; } = 30;

        [Category("Continuous tracing"), DisplayName("Maximum trace records"), Description("Pause automatic tracing at this limit. Export and clear to continue. Between 100 and 1,000,000 records.")]
        public int MaxRecords { get; set; } = 100000;

        [Category("Continuous tracing"), DisplayName("Pause at breakpoints"), Description("Exceptions and user breaks always pause automatic tracing.")]
        public bool PauseAtBreakpoints { get; set; } = true;

        internal TraceSettings Snapshot() => new TraceSettings
        {
            DelayMilliseconds = Math.Max(1, Math.Min(5000, DelayMilliseconds)),
            MaxRecords = Math.Max(100, Math.Min(1000000, MaxRecords)),
            PauseAtBreakpoints = PauseAtBreakpoints
        };

        protected override void OnApply(PageApplyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var validated = Snapshot();
            DelayMilliseconds = validated.DelayMilliseconds;
            MaxRecords = validated.MaxRecords;
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply) Applied?.Invoke(this, EventArgs.Empty);
        }
    }
}
