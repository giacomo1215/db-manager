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

        /// <summary>
        /// When true, Run wraps the script in an explicit transaction and leaves it
        /// pending for the user to Commit or Rollback (SSMS-style), instead of
        /// autocommitting each statement. The in-editor toggle reflects and persists
        /// this value.
        /// </summary>
        public bool TransactionMode { get; set; }
    }
}
