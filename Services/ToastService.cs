using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DbManager.Abstractions;
using Microsoft.Extensions.Logging;

namespace DbManager.Services
{
    /// <summary>
    /// Scoped toast queue. Toasts auto-expire via a background timer that raises
    /// Changed so the host component re-renders.
    ///
    /// Doubles as the app's error sink: a Danger/Warning toast is the point where
    /// a caught error becomes visible to the user, so it is also written to the
    /// text log. This catches the errors raised straight in pages (e.g. the
    /// connection-dialog "Test connection" failure) that never pass through a
    /// service, without touching every page. Error detail lives in the toast
    /// message; the services that swallow DB exceptions log the full stack.
    /// </summary>
    public class ToastService : IToastService, IDisposable
    {
        private readonly List<ToastMessage> _toasts = [];
        private readonly ILogger<ToastService> _logger;
        private Timer? _sweepTimer;

        public ToastService(ILogger<ToastService> logger) => _logger = logger;

        public IReadOnlyList<ToastMessage> Toasts => _toasts;

        public event Action? Changed;

        public void Show(string title, string message, ToastKind kind = ToastKind.Danger, int displaySeconds = 8)
        {
            if (kind is ToastKind.Danger or ToastKind.Warning)
                _logger.LogError("{Title}: {Message}", title, message);

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