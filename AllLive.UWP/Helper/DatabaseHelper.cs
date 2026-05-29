using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Windows.Storage;
using System.IO;
using AllLive.UWP.Models;

namespace AllLive.UWP.Helper
{

    public static class DatabaseHelper
    {
        static SqliteConnection db;
        public async static Task InitializeDatabase()
        {
            await ApplicationData.Current.LocalFolder.CreateFileAsync("alllive.db", CreationCollisionOption.OpenIfExists);
            string dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "alllive.db");
            db = new SqliteConnection($"Filename={dbPath}");
            db.Open();
            string tableCommand = @"CREATE TABLE IF NOT EXISTS Favorite (
id INTEGER PRIMARY KEY AUTOINCREMENT, 
user_name TEXT,
site_name TEXT,
photo TEXT,
room_id TEXT,
sort_order INTEGER DEFAULT 0);

CREATE TABLE IF NOT EXISTS History (
id INTEGER PRIMARY KEY AUTOINCREMENT, 
user_name TEXT,
site_name TEXT,
photo TEXT,
room_id TEXT,
watch_time DATETIME);
";
            using (var createTable = new SqliteCommand(tableCommand, db))
            {
                createTable.ExecuteNonQuery();
            }
            EnsureFavoriteSortOrderColumn();

        }

        private static void EnsureFavoriteSortOrderColumn()
        {
            try
            {
                bool hasSort = false;
                using (var command = new SqliteCommand("PRAGMA table_info(Favorite);", db))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = reader.GetString(1);
                        if (string.Equals(name, "sort_order", StringComparison.OrdinalIgnoreCase))
                        {
                            hasSort = true;
                            break;
                        }
                    }
                }

                if (!hasSort)
                {
                    using (var alter = new SqliteCommand("ALTER TABLE Favorite ADD COLUMN sort_order INTEGER DEFAULT 0;", db))
                    {
                        alter.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log("初始化关注排序字段失败", LogType.ERROR, ex);
            }
        }


        public static void AddFavorite(FavoriteItem item)
        {
            if (CheckFavorite(item.RoomID, item.SiteName)!=null) { return; }
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "INSERT INTO Favorite VALUES (NULL,@user_name,@site_name, @photo, @room_id, @sort_order);";
                command.Parameters.AddWithValue("@user_name", item.UserName);
                command.Parameters.AddWithValue("@site_name", item.SiteName);
                command.Parameters.AddWithValue("@photo", item.Photo);
                command.Parameters.AddWithValue("@room_id", item.RoomID);
                command.Parameters.AddWithValue("@sort_order", item.SortOrder);
                command.ExecuteNonQuery();
            }
        }
        public static void UpdateFavoriteSort(long id, int sortOrder)
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "UPDATE Favorite SET sort_order=@sort_order WHERE id=@id";
                command.Parameters.AddWithValue("@sort_order", sortOrder);
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }
        public static long? CheckFavorite(string roomId, string siteName)
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "SELECT * FROM Favorite WHERE room_id=@room_id and site_name=@site_name";
                command.Parameters.AddWithValue("@site_name", siteName);
                command.Parameters.AddWithValue("@room_id", roomId);
                var result = command.ExecuteScalar();
                if (result==null)
                {
                    return null;
                }
                return (long)result;
            }
        }
        public static void DeleteFavorite(long id)
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "DELETE FROM Favorite WHERE id=@id";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }

        }

        public static void DeleteFavorite()
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "DELETE FROM Favorite";
                command.ExecuteNonQuery();
            }

        }

        public async static Task<List<FavoriteItem>> GetFavorites()
        {
            List<FavoriteItem> favoriteItems = new List<FavoriteItem>();
            using (var command = new SqliteCommand("SELECT * FROM Favorite", db))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    favoriteItems.Add(new FavoriteItem()
                    {
                        ID= reader.GetInt32(0),
                        RoomID = reader.GetString(4),
                        Photo = reader.GetString(3),
                        SiteName = reader.GetString(2),
                        UserName = reader.GetString(1),
                        SortOrder = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetInt32(5) : 0
                    });
                }
            }
            return favoriteItems;
        }


        public static void AddHistory(HistoryItem item)
        {
            var hisId = CheckHistory(item.RoomID, item.SiteName);
            if (hisId != null)
            {
                using (var command = new SqliteCommand())
                {
                    command.Connection = db;
                    //更新时间
                    command.CommandText = "UPDATE History SET watch_time=@time WHERE room_id=@room_id and site_name=@site_name";
                    command.Parameters.AddWithValue("@site_name", item.SiteName);
                    command.Parameters.AddWithValue("@room_id", item.RoomID);
                    command.Parameters.AddWithValue("@time", DateTime.Now);
                    command.ExecuteNonQuery();
                }

                return;
            }

            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "INSERT INTO History VALUES (NULL,@user_name,@site_name, @photo, @room_id,@time);";
                command.Parameters.AddWithValue("@user_name", item.UserName);
                command.Parameters.AddWithValue("@site_name", item.SiteName);
                command.Parameters.AddWithValue("@photo", item.Photo);
                command.Parameters.AddWithValue("@room_id", item.RoomID);
                command.Parameters.AddWithValue("@time", DateTime.Now);
                command.ExecuteNonQuery();
            }
        }
        public static long? CheckHistory(string roomId, string siteName)
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "SELECT * FROM History WHERE room_id=@room_id and site_name=@site_name";
                command.Parameters.AddWithValue("@site_name", siteName);
                command.Parameters.AddWithValue("@room_id", roomId);
                var result = command.ExecuteScalar();
                if (result == null)
                {
                    return null;
                }
                return (long)result;
            }
        }
        public static void DeleteHistory(long id)
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "DELETE FROM History WHERE id=@id";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
          
        }
        public static void DeleteHistory()
        {
            using (var command = new SqliteCommand())
            {
                command.Connection = db;
                command.CommandText = "DELETE FROM History";
                command.ExecuteNonQuery();
            }

        }
        public async static Task<List<HistoryItem>> GetHistory()
        {
            List<HistoryItem> favoriteItems = new List<HistoryItem>();
            using (var command = new SqliteCommand("SELECT * FROM History ORDER BY watch_time DESC", db))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    favoriteItems.Add(new HistoryItem()
                    {
                        ID= reader.GetInt32(0),
                        RoomID = reader.GetString(4),
                        Photo = reader.GetString(3),
                        SiteName = reader.GetString(2),
                        UserName = reader.GetString(1),
                        WatchTime= reader.GetDateTime(5)
                    });
                }
            }
            return favoriteItems;
        }

    }


}
