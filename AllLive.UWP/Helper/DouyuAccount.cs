using AllLive.Core.Helper;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AllLive.UWP.Helper
{
    internal static class DouyuAccount
    {
        private const string FileName = "douyu-account.dat";
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private static bool initialized;
        private static int refreshing;
        internal static event EventHandler Changed;

        internal static async Task InitializeAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (initialized) return;
                initialized = true;
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) as StorageFile;
                if (file == null) return;
                var encrypted = await FileIO.ReadBufferAsync(file);
                var plain = await new DataProtectionProvider().UnprotectAsync(encrypted);
                var cookie = CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plain);
                if (DouyuAccountSession.ContainsLogin(cookie)) DouyuAccountState.SetCookie(cookie);
            }
            catch (Exception ex)
            {
                LogHelper.LogDebug(() => "[DouyuAccount] event=LoadFailed error=" + ex.GetType().Name);
            }
            finally { gate.Release(); }
        }

        internal static async Task<bool> TrySaveAsync(string cookie, DouyuAccountSession expected, CancellationToken token)
        {
            if (!DouyuAccountSession.ContainsLogin(cookie)) throw new InvalidOperationException("斗鱼登录凭据无效");
            var buffer = CryptographicBuffer.ConvertStringToBinary(cookie, BinaryStringEncoding.Utf8);
            var encrypted = await new DataProtectionProvider("LOCAL=user").ProtectAsync(buffer);
            await gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!DouyuAccountState.IsCurrent(expected)) return false;
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.OpenIfExists);
                using (var transaction = await file.OpenTransactedWriteAsync())
                {
                    transaction.Stream.Size = 0;
                    using (var writer = new DataWriter(transaction.Stream))
                    {
                        writer.WriteBuffer(encrypted);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }
                    token.ThrowIfCancellationRequested();
                    // The commit is the linearization point: an older refresh cannot undo logout.
                    await transaction.CommitAsync();
                }
                DouyuAccountState.SetCookie(cookie);
            }
            finally { gate.Release(); }
            RaiseChanged();
            return true;
        }

        internal static async Task LogoutAsync()
        {
            await gate.WaitAsync();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName);
                if (file != null) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                DouyuAccountState.SetCookie("");
            }
            finally { gate.Release(); }
            RaiseChanged();
        }

        internal static async Task RefreshAsync()
        {
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
            try
            {
                var session = DouyuAccountState.Capture();
                if (!session.HasLogin) return;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var cookie = await new DouyuAuthClient(session, renew: true).RefreshAsync(timeout.Token);
                    if (cookie != session.Cookie) await TrySaveAsync(cookie, session, timeout.Token);
                }
            }
            catch (Exception ex)
            {
                // A timeout or API change does not prove the saved account is invalid.
                LogHelper.LogDebug(() => "[DouyuAccount] event=RenewalDeferred error=" + ex.GetType().Name);
            }
            finally { Interlocked.Exchange(ref refreshing, 0); }
        }

        private static void RaiseChanged()
        {
            var handlers = Changed;
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(null, EventArgs.Empty); }
                catch (Exception ex) { LogHelper.LogDebug(() => "[DouyuAccount] event=NotifyFailed error=" + ex.GetType().Name); }
            }
        }
    }
}
