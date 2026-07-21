using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AllLive.UWP.Helper
{
    /// <summary>
    /// 全局图片降亮度（默认 70%），深色主题下更护眼。
    /// 通过 Opacity 近似亮度；绑定 Source={StaticResource ImageDimState} 的 Opacity。
    /// </summary>
    public sealed class ImageDimHelper : INotifyPropertyChanged
    {
        public const double DimOpacity = 0.7;

        /// <summary>
        /// App.xaml 中创建的资源实例；构造后可用。
        /// </summary>
        public static ImageDimHelper Current { get; private set; }

        public ImageDimHelper()
        {
            Current = this;
            _enabled = SettingHelper.GetValue(SettingHelper.IMAGE_DIM_ENABLED, true);
        }

        private bool _enabled;

        public bool Enabled
        {
            get { return _enabled; }
            private set
            {
                if (_enabled == value)
                {
                    return;
                }
                _enabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Opacity));
            }
        }

        /// <summary>
        /// 供 Image / ImageEx / PersonPicture 绑定的不透明度。
        /// </summary>
        public double Opacity
        {
            get { return _enabled ? DimOpacity : 1.0; }
        }

        public void SetEnabled(bool enabled, bool save = true)
        {
            if (save)
            {
                SettingHelper.SetValue(SettingHelper.IMAGE_DIM_ENABLED, enabled);
            }
            // 即使值相同也同步 Enabled 路径上的 UI（首次/还原）
            if (_enabled == enabled)
            {
                OnPropertyChanged(nameof(Enabled));
                OnPropertyChanged(nameof(Opacity));
                return;
            }
            Enabled = enabled;
        }

        public void ReloadFromSettings()
        {
            SetEnabled(SettingHelper.GetValue(SettingHelper.IMAGE_DIM_ENABLED, true), save: false);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
