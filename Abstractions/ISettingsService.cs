using DbManager.Models;

namespace DbManager.Abstractions
{
    /// <summary>
    /// User preferences backed by a plain JSON file (non-secret; readable while
    /// the vault is locked). The implementation caches the last-loaded values so
    /// reads are cheap and never touch disk on the hot path.
    /// </summary>
    public interface ISettingsService
    {
        /// <summary>
        /// The current preferences. Never null: a missing or corrupted file
        /// yields defaults rather than an exception.
        /// </summary>
        AppSettings Current { get; }

        /// <summary>
        /// Persists the given preferences and updates <see cref="Current"/>.
        /// Throws only if the file cannot be written (surfaced to the UI as a
        /// toast); validation is the caller's concern.
        /// </summary>
        void Save(AppSettings settings);
    }
}
