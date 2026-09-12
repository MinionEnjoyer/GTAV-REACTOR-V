using System;
using System.Threading;

namespace ReactorV.BootstrapHost
{
    /// <summary>
    /// Serializes one bootstrap attachment attempt without turning a failed
    /// attempt into a permanent no-retry latch. Disposal wakes waiters and
    /// prevents any later retry.
    /// </summary>
    public sealed class BootstrapAttachmentGate
    {
        private readonly object _sync = new object();
        private bool _attaching;
        private bool _attached;
        private bool _disposed;

        public bool TryAttach(Func<bool> attach)
        {
            return TryAttach(attach, () => true);
        }

        public bool TryAttach(Func<bool> attach, Func<bool> attachedIsHealthy)
        {
            if (attach == null) throw new ArgumentNullException(nameof(attach));
            if (attachedIsHealthy == null)
                throw new ArgumentNullException(nameof(attachedIsHealthy));
            lock (_sync)
            {
                while (_attaching && !_disposed)
                    Monitor.Wait(_sync);
                if (_disposed) return false;
                if (_attached) return attachedIsHealthy();
                _attaching = true;
            }

            var attached = false;
            try
            {
                attached = attach();
                return attached && CompleteAttachment();
            }
            finally
            {
                lock (_sync)
                {
                    _attaching = false;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                Monitor.PulseAll(_sync);
            }
        }

        private bool CompleteAttachment()
        {
            lock (_sync)
            {
                if (_disposed) return false;
                _attached = true;
                return true;
            }
        }
    }
}
