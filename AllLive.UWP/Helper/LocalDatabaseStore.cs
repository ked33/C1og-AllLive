using System;
using System.Collections.Generic;
using System.Linq;
using AllLive.UWP.Models;
using Microsoft.Data.Sqlite;

namespace AllLive.UWP.Helper
{
    // A connection belongs to this store. Every command and reader is completed under the same lock.
    internal sealed class LocalDatabaseStore : IDisposable
    {
        private readonly object sync = new object();
        private readonly SqliteConnection connection;

        internal LocalDatabaseStore(string databasePath)
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            try
            {
                connection.Open();
                InitializeSchema();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private void InitializeSchema()
        {
            using (var transaction = connection.BeginTransaction())
            {
                Execute(@"CREATE TABLE IF NOT EXISTS Favorite (
id INTEGER PRIMARY KEY AUTOINCREMENT, user_name TEXT, site_name TEXT, photo TEXT,
room_id TEXT, sort_order INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS History (
id INTEGER PRIMARY KEY AUTOINCREMENT, user_name TEXT, site_name TEXT, photo TEXT,
room_id TEXT, watch_time DATETIME);", transaction);

                var hasSortOrder = false;
                using (var command = CreateCommand("PRAGMA table_info(Favorite);", transaction))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), "sort_order", StringComparison.OrdinalIgnoreCase))
                        {
                            hasSortOrder = true;
                        }
                    }
                }
                if (!hasSortOrder)
                {
                    Execute("ALTER TABLE Favorite ADD COLUMN sort_order INTEGER DEFAULT 0;", transaction);
                }

                using (var command = CreateCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('IX_Favorite_SiteRoom','IX_History_SiteRoom');", transaction))
                {
                    if (Convert.ToInt32(command.ExecuteScalar()) == 2)
                    {
                        transaction.Commit();
                        return;
                    }
                }

                // Preserve every original duplicate in an archive before merging. The live rows retain
                // the oldest favorite ID/highest sort order and the most recent history entry.
                Execute(@"CREATE TABLE IF NOT EXISTS FavoriteDuplicateArchive AS SELECT * FROM Favorite WHERE 0;
INSERT INTO FavoriteDuplicateArchive
SELECT f.* FROM Favorite f WHERE f.site_name IS NOT NULL AND f.room_id IS NOT NULL
AND (SELECT COUNT(*) FROM Favorite x WHERE x.site_name=f.site_name AND x.room_id=f.room_id)>1;
UPDATE Favorite SET sort_order=(SELECT MAX(COALESCE(x.sort_order,0)) FROM Favorite x
WHERE x.site_name=Favorite.site_name AND x.room_id=Favorite.room_id)
WHERE site_name IS NOT NULL AND room_id IS NOT NULL
AND id=(SELECT MIN(x.id) FROM Favorite x WHERE x.site_name=Favorite.site_name AND x.room_id=Favorite.room_id);
DELETE FROM Favorite WHERE site_name IS NOT NULL AND room_id IS NOT NULL
AND id<>(SELECT MIN(x.id) FROM Favorite x WHERE x.site_name=Favorite.site_name AND x.room_id=Favorite.room_id);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Favorite_SiteRoom ON Favorite(site_name, room_id);

CREATE TABLE IF NOT EXISTS HistoryDuplicateArchive AS SELECT * FROM History WHERE 0;
INSERT INTO HistoryDuplicateArchive
SELECT h.* FROM History h WHERE h.site_name IS NOT NULL AND h.room_id IS NOT NULL
AND (SELECT COUNT(*) FROM History x WHERE x.site_name=h.site_name AND x.room_id=h.room_id)>1;
DELETE FROM History WHERE site_name IS NOT NULL AND room_id IS NOT NULL
AND id<>(SELECT x.id FROM History x WHERE x.site_name=History.site_name AND x.room_id=History.room_id
ORDER BY x.watch_time DESC, x.id DESC LIMIT 1);
CREATE UNIQUE INDEX IF NOT EXISTS IX_History_SiteRoom ON History(site_name, room_id);", transaction);
                transaction.Commit();
            }
        }

        internal void AddFavorite(FavoriteItem item)
        {
            var snapshot = SnapshotFavorites(new[] { item });
            ImportFavorites(snapshot, false, false);
        }

        internal void ImportFavorites(IEnumerable<FavoriteItem> items, bool replace, bool updateExistingSort)
        {
            // Enumerate and validate all entries before acquiring the connection or deleting anything.
            var snapshot = SnapshotFavorites(items);
            lock (sync)
            using (var transaction = connection.BeginTransaction())
            using (var insert = CreateFavoriteInsert(transaction))
            using (var update = CreateCommand("UPDATE Favorite SET sort_order=@sort_order WHERE site_name=@site_name AND room_id=@room_id;", transaction))
            {
                update.Parameters.Add("@sort_order", SqliteType.Integer);
                update.Parameters.Add("@site_name", SqliteType.Text);
                update.Parameters.Add("@room_id", SqliteType.Text);
                if (replace)
                {
                    Execute("DELETE FROM Favorite;", transaction);
                }
                foreach (var item in snapshot)
                {
                    BindFavorite(insert, item);
                    var inserted = insert.ExecuteNonQuery();
                    if (inserted == 0 && updateExistingSort)
                    {
                        update.Parameters["@sort_order"].Value = item.SortOrder;
                        update.Parameters["@site_name"].Value = item.SiteName;
                        update.Parameters["@room_id"].Value = item.RoomID;
                        update.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        internal void AddHistory(HistoryItem item)
        {
            if (item == null) throw new ArgumentException("历史记录包含空条目", nameof(item));
            WriteHistory(SnapshotHistory(new[] { new HistoryItem
            {
                RoomID = item.RoomID, SiteName = item.SiteName, UserName = item.UserName,
                Photo = item.Photo, WatchTime = DateTime.Now
            } }), false, false);
        }

        internal void ImportHistory(IEnumerable<HistoryItem> items, bool replace)
        {
            WriteHistory(SnapshotHistory(items), replace, true);
        }

        private void WriteHistory(List<HistoryItem> snapshot, bool replace, bool keepLatest)
        {
            lock (sync)
            using (var transaction = connection.BeginTransaction())
            using (var insert = CreateCommand(@"INSERT OR IGNORE INTO History
(user_name,site_name,photo,room_id,watch_time) VALUES (@user_name,@site_name,@photo,@room_id,@watch_time);", transaction))
            using (var update = CreateCommand(@"UPDATE History SET user_name=@user_name,photo=@photo,watch_time=@watch_time
WHERE site_name=@site_name AND room_id=@room_id AND (@keep_latest=0 OR watch_time IS NULL OR watch_time<=@watch_time);", transaction))
            {
                AddHistoryParameters(insert);
                AddHistoryParameters(update);
                update.Parameters.AddWithValue("@keep_latest", keepLatest ? 1 : 0);
                if (replace)
                {
                    Execute("DELETE FROM History;", transaction);
                }
                foreach (var item in snapshot)
                {
                    BindHistory(insert, item);
                    if (insert.ExecuteNonQuery() == 0)
                    {
                        BindHistory(update, item);
                        update.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
        }

        internal List<FavoriteItem> GetFavorites()
        {
            lock (sync)
            using (var command = CreateCommand("SELECT id,room_id,photo,site_name,user_name,sort_order FROM Favorite ORDER BY id;"))
            using (var reader = command.ExecuteReader())
            {
                var items = new List<FavoriteItem>();
                while (reader.Read())
                {
                    items.Add(new FavoriteItem
                    {
                        ID = reader.GetInt32(0), RoomID = ReadText(reader, 1), Photo = ReadText(reader, 2),
                        SiteName = ReadText(reader, 3), UserName = ReadText(reader, 4),
                        SortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                    });
                }
                return items;
            }
        }

        internal List<HistoryItem> GetHistory()
        {
            lock (sync)
            using (var command = CreateCommand("SELECT id,room_id,photo,site_name,user_name,watch_time FROM History ORDER BY watch_time DESC,id DESC;"))
            using (var reader = command.ExecuteReader())
            {
                var items = new List<HistoryItem>();
                while (reader.Read())
                {
                    items.Add(new HistoryItem
                    {
                        ID = reader.GetInt32(0), RoomID = ReadText(reader, 1), Photo = ReadText(reader, 2),
                        SiteName = ReadText(reader, 3), UserName = ReadText(reader, 4),
                        WatchTime = reader.IsDBNull(5) ? DateTime.MinValue : reader.GetDateTime(5)
                    });
                }
                return items;
            }
        }

        internal long? CheckFavorite(string roomId, string siteName) { return FindId("Favorite", roomId, siteName); }
        internal long? CheckHistory(string roomId, string siteName) { return FindId("History", roomId, siteName); }
        internal void DeleteFavorite(long? id = null) { Delete("Favorite", id); }
        internal void DeleteHistory(long? id = null) { Delete("History", id); }

        internal void UpdateFavoriteSort(long id, int sortOrder)
        {
            lock (sync)
            using (var command = CreateCommand("UPDATE Favorite SET sort_order=@sort_order WHERE id=@id;"))
            {
                command.Parameters.AddWithValue("@sort_order", sortOrder);
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }

        // Table names passed to these helpers are internal constants, never imported data.
        private long? FindId(string table, string roomId, string siteName)
        {
            lock (sync)
            using (var command = CreateCommand("SELECT id FROM " + table + " WHERE site_name=@site_name AND room_id=@room_id LIMIT 1;"))
            {
                command.Parameters.AddWithValue("@site_name", (object)siteName ?? DBNull.Value);
                command.Parameters.AddWithValue("@room_id", (object)roomId ?? DBNull.Value);
                var result = command.ExecuteScalar();
                return result == null || result == DBNull.Value ? (long?)null : Convert.ToInt64(result);
            }
        }

        private void Delete(string table, long? id)
        {
            lock (sync)
            using (var command = CreateCommand("DELETE FROM " + table + (id.HasValue ? " WHERE id=@id;" : ";")))
            {
                if (id.HasValue) command.Parameters.AddWithValue("@id", id.Value);
                command.ExecuteNonQuery();
            }
        }

        internal static List<FavoriteItem> SnapshotFavorites(IEnumerable<FavoriteItem> items)
        {
            if (items == null) throw new ArgumentException("关注列表内容为空或格式无效", nameof(items));
            return items.Select(item =>
            {
                if (item == null) throw new ArgumentException("关注列表包含空条目", nameof(items));
                ValidateIdentity(item.SiteName, item.RoomID);
                return new FavoriteItem
                {
                    RoomID = item.RoomID, SiteName = item.SiteName, UserName = item.UserName ?? string.Empty,
                    Photo = item.Photo ?? string.Empty, SortOrder = item.SortOrder
                };
            }).ToList();
        }

        internal static List<HistoryItem> SnapshotHistory(IEnumerable<HistoryItem> items)
        {
            if (items == null) throw new ArgumentException("历史记录内容为空或格式无效", nameof(items));
            return items.Select(item =>
            {
                if (item == null) throw new ArgumentException("历史记录包含空条目", nameof(items));
                ValidateIdentity(item.SiteName, item.RoomID);
                if (item.WatchTime == DateTime.MinValue) throw new ArgumentException("历史记录的观看时间无效", nameof(items));
                return new HistoryItem
                {
                    RoomID = item.RoomID, SiteName = item.SiteName, UserName = item.UserName ?? string.Empty,
                    Photo = item.Photo ?? string.Empty,
                    WatchTime = item.WatchTime.Kind == DateTimeKind.Utc ? item.WatchTime.ToLocalTime() : item.WatchTime
                };
            }).ToList();
        }

        private static void ValidateIdentity(string siteName, string roomId)
        {
            if (string.IsNullOrWhiteSpace(siteName) || siteName == "未知" || string.IsNullOrWhiteSpace(roomId))
            {
                throw new ArgumentException("导入条目的平台或房间标识无效");
            }
        }

        private SqliteCommand CreateFavoriteInsert(SqliteTransaction transaction)
        {
            var command = CreateCommand(@"INSERT OR IGNORE INTO Favorite
(user_name,site_name,photo,room_id,sort_order) VALUES (@user_name,@site_name,@photo,@room_id,@sort_order);", transaction);
            command.Parameters.Add("@user_name", SqliteType.Text);
            command.Parameters.Add("@site_name", SqliteType.Text);
            command.Parameters.Add("@photo", SqliteType.Text);
            command.Parameters.Add("@room_id", SqliteType.Text);
            command.Parameters.Add("@sort_order", SqliteType.Integer);
            return command;
        }

        private static void BindFavorite(SqliteCommand command, FavoriteItem item)
        {
            command.Parameters["@user_name"].Value = item.UserName;
            command.Parameters["@site_name"].Value = item.SiteName;
            command.Parameters["@photo"].Value = item.Photo;
            command.Parameters["@room_id"].Value = item.RoomID;
            command.Parameters["@sort_order"].Value = item.SortOrder;
        }

        private static void AddHistoryParameters(SqliteCommand command)
        {
            command.Parameters.Add("@user_name", SqliteType.Text);
            command.Parameters.Add("@site_name", SqliteType.Text);
            command.Parameters.Add("@photo", SqliteType.Text);
            command.Parameters.Add("@room_id", SqliteType.Text);
            command.Parameters.Add("@watch_time", SqliteType.Text);
        }

        private static void BindHistory(SqliteCommand command, HistoryItem item)
        {
            command.Parameters["@user_name"].Value = item.UserName;
            command.Parameters["@site_name"].Value = item.SiteName;
            command.Parameters["@photo"].Value = item.Photo;
            command.Parameters["@room_id"].Value = item.RoomID;
            command.Parameters["@watch_time"].Value = item.WatchTime;
        }

        private SqliteCommand CreateCommand(string sql, SqliteTransaction transaction = null)
        {
            return new SqliteCommand(sql, connection, transaction);
        }

        private void Execute(string sql, SqliteTransaction transaction)
        {
            using (var command = CreateCommand(sql, transaction)) command.ExecuteNonQuery();
        }

        private static string ReadText(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        public void Dispose()
        {
            lock (sync) connection.Dispose();
        }
    }
}
