using System;

namespace SourceTrace.Core
{
    // UI/file-operation feedback has no authority to change debugger state.
    public sealed class OperationStatus
    {
        private long generation;
        public string Message { get; private set; } = "";
        public event EventHandler Changed;
        public long Begin(string message) { long ticket = ++generation; Set(message); return ticket; }
        public void Complete(long ticket, string message) { if (ticket == generation) Set(message); }
        private void Set(string message) { Message = message; Changed?.Invoke(this, EventArgs.Empty); }
    }
}
