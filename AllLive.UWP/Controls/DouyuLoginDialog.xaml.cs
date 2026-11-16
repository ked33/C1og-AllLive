using AllLive.Core.Helper;
using AllLive.UWP.Helper;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace AllLive.UWP.Controls
{
    public sealed partial class DouyuLoginDialog : ContentDialog
    {
        private CancellationTokenSource flow;

        public DouyuLoginDialog()
        {
            InitializeComponent();
            ThemeHelper.ApplyElementTheme(this);
            Loaded += (sender, args) => StartFlow();
            Closed += (sender, args) => StopFlow();
            Unloaded += (sender, args) => StopFlow();
        }

        private void Refresh_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            StartFlow();
        }

        private void StopFlow()
        {
            var previous = flow;
            flow = null;
            previous?.Cancel(); // Async owner disposes after the in-flight request has exited.
        }

        private async void StartFlow()
        {
            StopFlow();
            var owner = new CancellationTokenSource();
            flow = owner;
            var token = owner.Token;
            QrImage.Source = null;
            BusyRing.IsActive = true;
            StatusText.Text = "正在获取二维码…";
            try
            {
                var session = DouyuAccountState.Capture();
                var client = new DouyuAuthClient(session);
                var qr = await client.GenerateAsync(token);
                if (flow != owner || token.IsCancellationRequested) return;
                var writer = new ZXing.BarcodeWriter
                {
                    Format = ZXing.BarcodeFormat.QR_CODE,
                    Options = new ZXing.Common.EncodingOptions { Width = 260, Height = 260, Margin = 4 }
                };
                QrImage.Source = writer.Write(qr.Url);
                BusyRing.IsActive = false;
                StatusText.Text = "等待扫码";
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(qr.ExpiresIn))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    var result = await client.PollAsync(qr.Key, token);
                    if (flow != owner || token.IsCancellationRequested) return;
                    if (result.Status == DouyuQrStatus.Success)
                    {
                        StatusText.Text = "正在保存登录状态…";
                        if (!await DouyuAccount.TrySaveAsync(result.Cookie, session, token))
                        {
                            StatusText.Text = "登录状态已变化，请重新扫码";
                            return;
                        }
                        if (flow == owner) Hide();
                        return;
                    }
                    if (result.Status == DouyuQrStatus.Expired) break;
                    StatusText.Text = result.Status == DouyuQrStatus.Scanned ? "已扫码，请在手机上确认" : "等待扫码";
                }
                if (flow == owner)
                {
                    QrImage.Source = null;
                    StatusText.Text = "二维码已失效，请刷新";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (flow == owner)
                {
                    StatusText.Text = "登录未完成，请检查网络后刷新二维码重试";
                    LogHelper.LogDebug(() => "[DouyuAccount] event=LoginFailed error=" + ex.GetType().Name);
                }
            }
            finally
            {
                if (flow == owner) { flow = null; BusyRing.IsActive = false; }
                owner.Dispose();
            }
        }
    }
}
