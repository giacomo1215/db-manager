using System;
using System.Collections.Generic;

namespace DbManager.Abstractions
{
    public enum ToastKind
    {
        Info,
        Success,
        Warning,
        Danger
    }

    public record ToastMessage(Guid Id, string Title, string Message, ToastKind Kind, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Minimal replacement for the DevExpress toast of the original app: a scoped
    /// queue rendered by the Toasts host in the main layout, auto-expiring.
    /// </summary>
    public interface IToastService
    {
        IReadOnlyList<ToastMessage> Toasts { get; }

        /// <summary>Raised on every add/remove so the host can re-render.</summary>
        event Action? Changed;

        void Show(string title, string message, ToastKind kind = ToastKind.Danger, int displaySeconds = 8);

        void Dismiss(Guid id);
    }
}