namespace DbManager.Models
{
    /// <summary>
    /// User preferences, persisted as a plain (non-secret) JSON file beside the
    /// vault. Distinct from the encrypted connection store: these values are not
    /// sensitive and must be readable before the vault is unlocked.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// When true, Run executes only the highlighted text (SSMS-style); a
        /// collapsed caret or an empty selection falls back to the whole editor
        /// content. When false, Run always executes the whole script.
        /// </summary>
        public bool ExecuteSelectionOnly { get; set; } = true;

        /// <summary>
        /// Row count for the sidebar quick-run ("TOP N" / "LIMIT N") button.
        /// Clamped to a sane minimum so the generated query is always valid.
        /// </summary>
        public int QuickRowsLimit { get; set; } = 20;
    }
}
