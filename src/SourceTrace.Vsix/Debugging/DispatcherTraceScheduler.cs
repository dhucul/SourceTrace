using System;
using System.Windows.Threading;
using SourceTrace.Core;

namespace SourceTrace.Debugging
{
    internal sealed class DispatcherTraceScheduler : ITraceScheduler
    {
        private readonly DispatcherTimer timer;
        private Action pending;
        public DispatcherTraceScheduler()
        {
            timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Tick += Tick;
        }
        public void Schedule(Action callback, int delayMilliseconds)
        {
            Cancel();
            pending = callback;
            timer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
            timer.Start();
        }
        public void Cancel() { timer.Stop(); pending = null; }
        private void Tick(object sender, EventArgs args)
        {
            var callback = pending;
            Cancel();
            callback?.Invoke();
        }
        public void Dispose() { Cancel(); timer.Tick -= Tick; }
    }
}
