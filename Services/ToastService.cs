using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DbManager.Abstractions;

namespace DbManager.Services
{
    /// <summary>
    /// Scoped toast queue. Toasts auto-expire via a background timer that raises
    /// Changed so the host component re-renders.
    /// </summary>
    public class ToastService : IToastService, IDisposable
    {
        private readonly List<ToastMessage> _toasts = [];
        private Timer? _sweepTimer;

        public IReadOnlyList<ToastMessage> Toasts => _toasts;

        public event Action? Changed;

        public void Show(string title, string message, ToastKind kind = ToastKind.Danger, int displaySeconds = 8)
        {
            _toasts.Add(new ToastMessage(Guid.NewGuid(), title, message, kind, DateTimeOffset.UtcNow.AddSeconds(displaySeconds)));
            // One shared sweeper started lazily; cheap given the handful of
            // toasts a session produces.
            _sweepTimer ??= new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Changed?.Invoke();
        }

        public void Dismiss(Guid id)
        {
            if (_toasts.RemoveAll(t => t.Id == id) > 0)
                Changed?.Invoke();
        }

        private void Sweep()
        {
            var expired = _toasts.RemoveAll(t => t.ExpiresAt <= DateTimeOffset.UtcNow);
            if (expired > 0)
                Changed?.Invoke();
        }

        public void Dispose()
        {
            _sweepTimer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}