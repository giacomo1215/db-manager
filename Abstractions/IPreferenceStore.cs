namespace DbManager.Abstractions
{
    /// <summary>
    /// Small, non-secret preferences stored in the app's SQLite file ("dblite")
    /// in the clear. Unlike the query library this needs NO vault key, so the
    /// values are readable before unlock and before the first paint — which is
    /// what lets the theme be rendered server-side without a flash.
    /// </summary>
    public interface IPreferenceStore
    {
        /// <summary>
        /// Active colour theme: "light" or "dark". Never null; defaults to
        /// "light" when the store is empty or unreadable.
        /// </summary>
        string Theme { get; set; }
    }
}
