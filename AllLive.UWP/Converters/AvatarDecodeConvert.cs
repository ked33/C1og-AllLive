using System;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;

namespace AllLive.UWP.Converters
{
    /// <summary>
    /// 头像 URL 转按目标尺寸解码的 BitmapImage。
    /// 头像原图可达 640x640，直接绑定字符串会按原图解码；
    /// 通过 ConverterParameter 指定逻辑像素宽度（默认 112）限制解码尺寸。
    /// </summary>
    public class AvatarDecodeConvert : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var url = value as string;
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }
            var width = 112;
            if (parameter is string param && int.TryParse(param, out var parsed) && parsed > 0)
            {
                width = parsed;
            }
            return new BitmapImage
            {
                DecodePixelWidth = width,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = uri
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
