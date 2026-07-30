using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace AllLive.UWP.Controls
{
    /// <summary>
    /// toolkit 7.x 的 ImageEx 虽然定义了 DecodePixelWidth/Height/Type 属性，
    /// 但默认的 ProvideCachedResourceAsync 直接 new BitmapImage(uri)，并不会应用这些属性，
    /// 导致封面按原图分辨率解码。这里覆写使解码尺寸真正生效。
    /// </summary>
    public class DecodedImageEx : ImageEx
    {
        protected override Task<ImageSource> ProvideCachedResourceAsync(Uri imageUri, CancellationToken token)
        {
            var bitmap = new BitmapImage();
            if (DecodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = DecodePixelWidth;
            }
            if (DecodePixelHeight > 0)
            {
                bitmap.DecodePixelHeight = DecodePixelHeight;
            }
            if (DecodePixelWidth > 0 || DecodePixelHeight > 0)
            {
                bitmap.DecodePixelType = DecodePixelType;
            }
            bitmap.UriSource = imageUri;
            return Task.FromResult((ImageSource)bitmap);
        }
    }
}
