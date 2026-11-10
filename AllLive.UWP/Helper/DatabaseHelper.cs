using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AllLive.UWP.Models;
using Windows.Storage;

namespace AllLive.UWP.Helper
{
    public static class DatabaseHelper
    {
        private static readonly object initializeSync = new object();
        private static LocalDatabaseStore store;

        public static async Task InitializeDatabase()
        {
            await ApplicationData.Current.LocalFolder.CreateFileAsync("alllive.db", CreationCollisionOption.OpenIfExists);
            var databasePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "alllive.db");
            await Task.Run(() =>
            {
                lock (initializeSync)
                {
                    if (store == null) store = new LocalDatabaseStore(databasePath);
                }
            });
        }

        private static LocalDatabaseStore Store
        {
            get
            {
                lock (initializeSync)
                {
                    return store ?? throw new InvalidOperationException("数据库尚未初始化");
                }
            }
        }

        public static void AddFavorite(FavoriteItem item) { Store.AddFavorite(item); }
        public static void UpdateFavoriteSort(long id, int sortOrder) { Store.UpdateFavoriteSort(id, sortOrder); }
        public static long? CheckFavorite(string roomId, string siteName) { return Store.CheckFavorite(roomId, siteName); }
        public static void DeleteFavorite(long id) { Store.DeleteFavorite(id); }
        public static void DeleteFavorite() { Store.DeleteFavorite(); }
        public static Task<List<FavoriteItem>> GetFavorites() { return Task.Run(() => Store.GetFavorites()); }

        public static Task ImportFavoritesAsync(IEnumerable<FavoriteItem> items, bool replace = false, bool updateExistingSort = true)
        {
            var snapshot = LocalDatabaseStore.SnapshotFavorites(items);
            return Task.Run(() => Store.ImportFavorites(snapshot, replace, updateExistingSort));
        }

        // Watching a room records now; importing history explicitly preserves its original timestamp.
        public static void AddHistory(HistoryItem item) { Store.AddHistory(item); }
        public static long? CheckHistory(string roomId, string siteName) { return Store.CheckHistory(roomId, siteName); }
        public static void DeleteHistory(long id) { Store.DeleteHistory(id); }
        public static void DeleteHistory() { Store.DeleteHistory(); }
        public static Task<List<HistoryItem>> GetHistory() { return Task.Run(() => Store.GetHistory()); }

        public static Task ImportHistoryAsync(IEnumerable<HistoryItem> items, bool replace = false)
        {
            var snapshot = LocalDatabaseStore.SnapshotHistory(items);
            return Task.Run(() => Store.ImportHistory(snapshot, replace));
        }
    }
}
