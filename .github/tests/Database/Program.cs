using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AllLive.UWP.Helper;
using AllLive.UWP.Models;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace AllLive.Tests
{
    public static class DatabaseTests
    {
        public static async Task Main()
        {
            await RunAsync();
        }

        public static async Task RunAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "AllLive-DatabaseTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                TestValidationAndRollback(Path.Combine(directory, "rollback.db"));
                TestHistoryTimestamps(Path.Combine(directory, "history.db"));
                TestDuplicateMigration(Path.Combine(directory, "migration.db"));
                TestLegacySchema(Path.Combine(directory, "legacy.db"));
                await TestConcurrentAccessAsync(Path.Combine(directory, "concurrent.db"));
                Console.WriteLine("PASS: database atomic imports, timestamp preservation, duplicate migration and concurrent access.");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, true);
            }
        }

        private static void TestValidationAndRollback(string path)
        {
            using (var store = new LocalDatabaseStore(path))
            {
                store.AddFavorite(Favorite("original", 3));
                AssertThrows(() => store.ImportFavorites(new[] { Favorite("new"), null }, true, true));
                AssertThrows(() => store.ImportFavorites(DataImportParser.ParseFavorites("[null]"), true, true));
                AssertThrows(() => store.ImportFavorites(DataImportParser.ParseFavorites("null"), true, true));
                AssertThrows(() => store.ImportFavorites(DataImportParser.ParseFavorites("[{\"siteId\":\"unsupported\",\"roomId\":\"1\"}]"), true, true));
                AssertThrows(() => store.ImportFavorites(DataImportParser.ParseFavorites("[{\"siteId\":\"huya\"}]"), true, true));
                AssertThrows(() => store.ImportFavorites(DataImportParser.ParseFavorites("[{\"siteId\":\"huya\",\"roomId\":\"new\",\"sort\":1.5}]"), true, true));
                Assert(store.GetFavorites().Single().RoomID == "original", "invalid favorites preserve the entire old collection");
                var compatible = DataImportParser.ParseFavorites("[{\"SiteId\":\"huya\",\"RoomId\":123,\"UserName\":\"name\",\"Face\":null,\"Sort\":\"7\"}]").Single();
                Assert(compatible.RoomID == "123" && compatible.SortOrder == 7 && compatible.UserName == "name", "legacy property casing, numeric room IDs and string sorting remain accepted");

                Execute(path, "CREATE TRIGGER fail_favorite BEFORE INSERT ON Favorite WHEN NEW.room_id='fail' BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
                AssertThrows(() => store.ImportFavorites(new[] { Favorite("first"), Favorite("fail") }, true, true));
                Assert(store.GetFavorites().Single().RoomID == "original", "mid-transaction failure rolls back deletion and partial inserts");
                store.ImportFavorites(new[] { Favorite("original", 9) }, false, true);
                Assert(store.GetFavorites().Single().SortOrder == 9, "local merge updates sorting without duplicates");
                store.ImportFavorites(new[] { Favorite("original", 1) }, false, false);
                Assert(store.GetFavorites().Single().SortOrder == 9, "sync merge can preserve existing sorting");

                store.ImportHistory(new[] { History("original", new DateTime(2024, 1, 2)) }, false);
                AssertThrows(() => store.ImportHistory(DataImportParser.ParseHistory("[{\"siteId\":\"huya\",\"roomId\":\"new\",\"updateTime\":\"invalid\"}]"), true));
                Execute(path, "CREATE TRIGGER fail_history BEFORE INSERT ON History WHEN NEW.room_id='fail' BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
                AssertThrows(() => store.ImportHistory(new[] { History("first", DateTime.Now), History("fail", DateTime.Now) }, true));
                Assert(store.GetHistory().Single().RoomID == "original", "history import rollback retains the original history");
            }
        }

        private static void TestHistoryTimestamps(string path)
        {
            var time = new DateTime(2021, 3, 4, 12, 34, 56).AddTicks(1234567);
            var json = JsonConvert.SerializeObject(new[] { new { siteId = "huya", roomId = "time", updateTime = time.ToString("o", CultureInfo.InvariantCulture) } });
            using (var store = new LocalDatabaseStore(path))
            {
                store.ImportHistory(DataImportParser.ParseHistory(json), true);
                Assert(store.GetHistory().Single().WatchTime == time, "history import preserves all timestamp digits");
                store.ImportHistory(new[] { History("time", time.AddDays(-1)) }, false);
                Assert(store.GetHistory().Single().WatchTime == time, "merging older history preserves the newer watch time");
                var before = DateTime.Now;
                store.AddHistory(History("time", time));
                var watched = store.GetHistory().Single().WatchTime;
                Assert(watched >= before && watched <= DateTime.Now, "normal viewing records now instead of an imported timestamp");
                store.ImportHistory(new[] { History("time", time) }, true);
                Assert(store.GetHistory().Single().WatchTime == time, "replace history restores exactly the incoming timestamp");
                store.ImportHistory(new[] { History("time", DateTime.Now.AddDays(7)) }, true);
                store.AddHistory(History("time", time));
                Assert(store.GetHistory().Single().WatchTime <= DateTime.Now, "normal viewing also repairs a future imported timestamp");

                var utc = DateTime.SpecifyKind(time, DateTimeKind.Utc);
                var utcJson = JsonConvert.SerializeObject(new[] { new { siteId = "huya", roomId = "utc", updateTime = utc.ToString("o", CultureInfo.InvariantCulture) } });
                var parsedUtc = DataImportParser.ParseHistory(utcJson).Single();
                Assert(parsedUtc.WatchTime == utc.ToLocalTime() && parsedUtc.WatchTime.Kind == DateTimeKind.Local,
                    "Z timestamps are normalized to the existing local-time storage convention");
                store.ImportHistory(new[] { parsedUtc }, true);
                Assert(store.GetHistory().Single().WatchTime == utc.ToLocalTime(), "UTC JSON stays at the same local instant after SQLite roundtrip");
                store.ImportHistory(new[] { History("utc-direct", utc) }, true);
                Assert(store.GetHistory().Single().WatchTime == utc.ToLocalTime(), "direct typed imports normalize UTC even without the JSON parser");

                var offset = new DateTimeOffset(time, TimeSpan.FromMinutes(330));
                var offsetJson = JsonConvert.SerializeObject(new[] { new { siteId = "huya", roomId = "offset", updateTime = offset.ToString("o", CultureInfo.InvariantCulture) } });
                store.ImportHistory(DataImportParser.ParseHistory(offsetJson), true);
                Assert(store.GetHistory().Single().WatchTime == offset.LocalDateTime, "explicit-offset timestamps preserve their local instant");
            }
        }

        private static void TestDuplicateMigration(string path)
        {
            Execute(path, @"CREATE TABLE Favorite (id INTEGER PRIMARY KEY AUTOINCREMENT,user_name TEXT,site_name TEXT,photo TEXT,room_id TEXT,sort_order INTEGER DEFAULT 0);
INSERT INTO Favorite VALUES(1,'first','虎牙直播','','same',2),(2,'second','虎牙直播','','same',9);
CREATE TABLE History (id INTEGER PRIMARY KEY AUTOINCREMENT,user_name TEXT,site_name TEXT,photo TEXT,room_id TEXT,watch_time DATETIME);
INSERT INTO History VALUES(1,'newest','虎牙直播','','same','2024-03-04 12:00:00'),(2,'oldest','虎牙直播','','same','2023-03-04 12:00:00');");
            using (var store = new LocalDatabaseStore(path))
            {
                var favorite = store.GetFavorites().Single();
                Assert(favorite.ID == 1 && favorite.SortOrder == 9, "favorite duplicate merge retains original identity and highest sort");
                Assert(store.GetHistory().Single().UserName == "newest", "history duplicate merge retains latest viewing record");
                Assert(Count(path, "FavoriteDuplicateArchive") == 2 && Count(path, "HistoryDuplicateArchive") == 2, "all original duplicate rows remain recoverable in archive tables");
                AssertThrows(() => Execute(path, "INSERT INTO Favorite(user_name,site_name,photo,room_id) VALUES('duplicate','虎牙直播','','same');"));
            }
            using (var store = new LocalDatabaseStore(path))
            {
                Assert(store.GetFavorites().Count == 1 && Count(path, "FavoriteDuplicateArchive") == 2, "migration is idempotent on next startup");
            }
        }

        private static void TestLegacySchema(string path)
        {
            Execute(path, @"CREATE TABLE Favorite (id INTEGER PRIMARY KEY AUTOINCREMENT,user_name TEXT,site_name TEXT,photo TEXT,room_id TEXT);
INSERT INTO Favorite VALUES(1,'legacy','虎牙直播',NULL,'room');");
            using (var store = new LocalDatabaseStore(path))
            {
                var favorite = store.GetFavorites().Single();
                Assert(favorite.SortOrder == 0 && favorite.Photo == string.Empty, "legacy schema and nullable metadata remain readable");
                store.AddFavorite(Favorite("quote'room"));
                Assert(store.CheckFavorite("quote'room", "虎牙直播").HasValue, "room identities are parameterized");
            }
        }

        private static async Task TestConcurrentAccessAsync(string path)
        {
            using (var store = new LocalDatabaseStore(path))
            {
                await Task.WhenAll(Enumerable.Range(0, 48).Select(index => Task.Run(() =>
                {
                    var room = (index % 4).ToString(CultureInfo.InvariantCulture);
                    store.AddFavorite(Favorite(room));
                    store.AddHistory(History(room, new DateTime(2020, 1, 1)));
                    store.GetFavorites();
                    store.GetHistory();
                })));
                Assert(store.GetFavorites().Count == 4 && store.GetHistory().Count == 4, "parallel windows cannot create duplicate rooms or overlap readers");
            }
        }

        private static FavoriteItem Favorite(string room, int sort = 0)
        {
            return new FavoriteItem { RoomID = room, SiteName = "虎牙直播", UserName = room, SortOrder = sort };
        }

        private static HistoryItem History(string room, DateTime time)
        {
            return new HistoryItem { RoomID = room, SiteName = "虎牙直播", UserName = room, WatchTime = time };
        }

        private static void Execute(string path, string sql)
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                using (var command = new SqliteCommand(sql, connection)) command.ExecuteNonQuery();
            }
        }

        private static long Count(string path, string table)
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                using (var command = new SqliteCommand("SELECT COUNT(*) FROM " + table, connection)) return (long)command.ExecuteScalar();
            }
        }

        private static void AssertThrows(Action action)
        {
            try { action(); }
            catch { return; }
            throw new InvalidOperationException("Expected operation to fail.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
