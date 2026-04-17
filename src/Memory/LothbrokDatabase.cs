using System;
using System.Data.SQLite;
using System.IO;

namespace LothbrokAI.Memory
{
    /// <summary>
    /// SQLite database layer for LothbrokAI memory v2.
    /// Manages the campaign-scoped memory index and hypergraph store.
    ///
    /// DESIGN: One database file per campaign save, co-located with the
    /// .lothbrok.json personality files. Schema is created on first access
    /// and migrated automatically on version bump.
    ///
    /// Vectors are stored as BLOB (serialized float[]) — no external
    /// vector database required. Cosine search runs in-process.
    /// </summary>
    public static class LothbrokDatabase
    {
        private static SQLiteConnection _connection;
        private static string _dbPath;

        // DESIGN: Schema version for future migrations
        private const int SCHEMA_VERSION = 1;

        // ================================================================
        // INIT / TEARDOWN
        // ================================================================

        /// <summary>
        /// Open (or create) the campaign database. Called on campaign load.
        /// </summary>
        public static void Open(string saveDataDir)
        {
            _dbPath = Path.Combine(saveDataDir, "lothbrok_memory.db");
            string connStr = $"Data Source={_dbPath};Version=3;";

            _connection = new SQLiteConnection(connStr);
            _connection.Open();

            ApplySchema();
            LothbrokSubModule.Log($"LothbrokDatabase opened: {_dbPath}");
        }

        /// <summary>
        /// Close the database connection. Called on campaign end / mod unload.
        /// </summary>
        public static void Close()
        {
            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
                _connection = null;
                LothbrokSubModule.Log("LothbrokDatabase closed.");
            }
        }

        /// <summary>
        /// Get the active connection. Throws if not initialized.
        /// </summary>
        public static SQLiteConnection GetConnection()
        {
            if (_connection == null)
                throw new InvalidOperationException("LothbrokDatabase not initialized. Call Open() first.");
            return _connection;
        }

        public static bool IsOpen => _connection != null;

        // ================================================================
        // SCHEMA
        // ================================================================

        private static void ApplySchema()
        {
            using (var cmd = _connection.CreateCommand())
            {
                // Memory vector index - all NPCs in one table for cross-NPC search
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS memories (
                        id          TEXT PRIMARY KEY,
                        npc_id      TEXT NOT NULL,
                        npc_name    TEXT NOT NULL,
                        text        TEXT NOT NULL,
                        vector      BLOB,
                        tags        TEXT,
                        game_day    INTEGER DEFAULT 0,
                        created_at  TEXT NOT NULL,
                        salience    REAL DEFAULT 1.0
                    );
                    CREATE INDEX IF NOT EXISTS idx_memories_npc ON memories(npc_id);
                    CREATE INDEX IF NOT EXISTS idx_memories_day ON memories(game_day);

                    -- Hypergraph node registry
                    CREATE TABLE IF NOT EXISTS hg_nodes (
                        id          TEXT PRIMARY KEY,
                        node_type   TEXT NOT NULL,   -- 'hero', 'clan', 'kingdom', 'concept'
                        label       TEXT NOT NULL
                    );

                    -- Hyperedge store (n-ary relationships)
                    CREATE TABLE IF NOT EXISTS hg_edges (
                        id              TEXT PRIMARY KEY,
                        label           TEXT NOT NULL,
                        activation_count INTEGER DEFAULT 0,
                        last_activated  TEXT,
                        created_at      TEXT NOT NULL
                    );

                    -- Junction table: which nodes belong to which edge
                    CREATE TABLE IF NOT EXISTS hg_edge_nodes (
                        edge_id     TEXT NOT NULL,
                        node_id     TEXT NOT NULL,
                        PRIMARY KEY (edge_id, node_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_hen_node ON hg_edge_nodes(node_id);

                    -- Schema version tracking
                    CREATE TABLE IF NOT EXISTS schema_meta (
                        key     TEXT PRIMARY KEY,
                        value   TEXT
                    );
                    INSERT OR IGNORE INTO schema_meta(key, value) VALUES('version', '1');
                ";
                cmd.ExecuteNonQuery();
            }

            LothbrokSubModule.Log($"DB schema v{SCHEMA_VERSION} applied.");
        }
    }
}
