using Dapper;
using Microsoft.Data.Sqlite;

namespace VupPlayer.Core.Data;

/// <summary>SQLite 连接工厂与迁移。WAL 模式，允许主程序和 MCP 进程同时访问。</summary>
public sealed class Database
{
    public string Path { get; }
    readonly string _connectionString;

    static Database()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Database(string? path = null)
    {
        Path = path ?? AppPaths.Database;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 10,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return conn;
    }

    /// <summary>创建或升级数据库结构。</summary>
    public void Migrate()
    {
        using var conn = Open();
        conn.Execute("PRAGMA journal_mode = WAL;");
        conn.Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
        var current = conn.ExecuteScalar<int?>("SELECT MAX(version) FROM schema_version") ?? 0;
        for (var v = current; v < Migrations.Scripts.Length; v++)
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(Migrations.Scripts[v], transaction: tx);
            conn.Execute("INSERT INTO schema_version(version) VALUES (@v)", new { v = v + 1 }, tx);
            tx.Commit();
        }
    }

    /// <summary>备份到 backups 目录，保留最近 keep 份。</summary>
    public string Backup(int keep = 10)
    {
        Directory.CreateDirectory(AppPaths.Backups);
        var target = System.IO.Path.Combine(AppPaths.Backups, $"library-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        using (var src = Open())
        using (var dst = new SqliteConnection($"Data Source={target}"))
        {
            dst.Open();
            src.BackupDatabase(dst);
        }
        SqliteConnection.ClearAllPools();
        foreach (var old in new DirectoryInfo(AppPaths.Backups).GetFiles("library-*.db")
                     .OrderByDescending(f => f.Name).Skip(keep))
        {
            try { old.Delete(); } catch { /* 忽略 */ }
        }
        return target;
    }
}

static class Migrations
{
    public static readonly string[] Scripts =
    [
        // v1：初始结构
        """
        CREATE TABLE videos (
          bvid        TEXT PRIMARY KEY,
          title       TEXT NOT NULL,
          up_mid      INTEGER NOT NULL,
          up_name     TEXT NOT NULL,
          cover_url   TEXT,
          description TEXT,
          pubdate     INTEGER,
          added_at    INTEGER NOT NULL,
          status      TEXT NOT NULL DEFAULT 'ok'
        );

        CREATE TABLE streamers (
          id      INTEGER PRIMARY KEY,
          name    TEXT NOT NULL UNIQUE COLLATE NOCASE,
          aliases TEXT,
          mid     INTEGER
        );

        CREATE TABLE tracks (
          id           INTEGER PRIMARY KEY,
          bvid         TEXT NOT NULL REFERENCES videos(bvid) ON DELETE CASCADE,
          cid          INTEGER NOT NULL,
          page         INTEGER NOT NULL,
          part_title   TEXT,
          duration_sec INTEGER,
          start_sec    REAL,
          end_sec      REAL,
          song_title   TEXT,
          streamer_id  INTEGER REFERENCES streamers(id) ON DELETE SET NULL,
          favorite     INTEGER NOT NULL DEFAULT 0,
          hidden       INTEGER NOT NULL DEFAULT 0,
          note         TEXT
        );
        CREATE INDEX ix_tracks_bvid ON tracks(bvid);
        CREATE UNIQUE INDEX ux_tracks_segment ON tracks(bvid, cid, IFNULL(start_sec, -1));

        CREATE TABLE tags (
          id   INTEGER PRIMARY KEY,
          name TEXT NOT NULL UNIQUE COLLATE NOCASE,
          kind TEXT NOT NULL DEFAULT 'custom'
        );
        CREATE TABLE track_tags (
          track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
          tag_id   INTEGER NOT NULL REFERENCES tags(id)   ON DELETE CASCADE,
          PRIMARY KEY (track_id, tag_id)
        );

        CREATE TABLE suggestions (
          id         INTEGER PRIMARY KEY,
          track_id   INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
          field      TEXT NOT NULL,
          value      TEXT NOT NULL,
          source     TEXT NOT NULL,
          confidence REAL,
          created_at INTEGER NOT NULL,
          state      TEXT NOT NULL DEFAULT 'pending'
        );

        CREATE TABLE playlists (
          id         INTEGER PRIMARY KEY,
          name       TEXT NOT NULL,
          created_at INTEGER NOT NULL
        );
        CREATE TABLE playlist_items (
          playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
          track_id    INTEGER NOT NULL REFERENCES tracks(id)    ON DELETE CASCADE,
          position    INTEGER NOT NULL,
          PRIMARY KEY (playlist_id, position)
        );

        CREATE TABLE sources (
          id          INTEGER PRIMARY KEY,
          kind        TEXT NOT NULL,
          remote_id   TEXT NOT NULL,
          title       TEXT,
          auto_sync   INTEGER NOT NULL DEFAULT 1,
          last_synced INTEGER,
          UNIQUE(kind, remote_id)
        );
        CREATE TABLE source_videos (
          source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
          bvid      TEXT    NOT NULL REFERENCES videos(bvid) ON DELETE CASCADE,
          PRIMARY KEY (source_id, bvid)
        );

        CREATE TABLE play_events (
          id         INTEGER PRIMARY KEY,
          track_id   INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
          started_at INTEGER NOT NULL,
          played_sec REAL NOT NULL,
          completed  INTEGER NOT NULL,
          skipped    INTEGER NOT NULL,
          mode       TEXT,
          context    TEXT
        );
        CREATE INDEX ix_play_events_track ON play_events(track_id);
        CREATE INDEX ix_play_events_time  ON play_events(started_at);
        """,

        // v2：推荐机制
        """
        -- 这次播放是谁选的（推荐器名 / manual / queue），以及推荐理由，用来比较不同算法
        ALTER TABLE play_events ADD COLUMN recommender TEXT;
        ALTER TABLE play_events ADD COLUMN reason TEXT;

        -- B 站发现池：按常听主播去 B 站搜到的、还没导入曲库的候选
        CREATE TABLE discovery (
          bvid          TEXT PRIMARY KEY,
          title         TEXT NOT NULL,
          uploader      TEXT,
          uploader_mid  INTEGER,
          duration_sec  INTEGER,
          play_count    INTEGER,
          pubdate       INTEGER,
          cover_url     TEXT,
          streamer      TEXT,            -- 按哪个主播搜到的
          query         TEXT,
          found_at      INTEGER NOT NULL,
          state         TEXT NOT NULL DEFAULT 'new',   -- new / imported / rejected
          state_at      INTEGER
        );
        CREATE INDEX ix_discovery_state ON discovery(state);

        CREATE TABLE meta (
          key   TEXT PRIMARY KEY,
          value TEXT
        );
        """,
    ];
}
