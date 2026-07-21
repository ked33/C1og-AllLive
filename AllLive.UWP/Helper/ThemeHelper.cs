using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Core;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace AllLive.UWP.Helper
{
    /// <summary>
    /// 应用显示模式 / 自定义主题配色。
    /// THEME: 0 跟随系统, 1 浅色, 2 深色, 3 One Dark Pro, 4 Nord, 5 Dracula, 6 Catppuccin Mocha, 7 纯黑
    /// </summary>
    public static class ThemeHelper
    {
        public const int ThemeSystem = 0;
        public const int ThemeLight = 1;
        public const int ThemeDark = 2;
        public const int ThemeOneDarkPro = 3;
        public const int ThemeNord = 4;
        public const int ThemeDracula = 5;
        public const int ThemeCatppuccinMocha = 6;
        public const int ThemeTrueBlack = 7;
        public const int ThemeMax = ThemeTrueBlack;

        private static readonly HashSet<string> AppliedKeys = new HashSet<string>(StringComparer.Ordinal);
        // 覆盖前的原值：null 表示原先不存在，清除时应 Remove
        private static readonly Dictionary<string, object> OriginalAppValues = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly Dictionary<string, object> OriginalDarkThemeValues = new Dictionary<string, object>(StringComparer.Ordinal);
        private static int _lastAppliedTheme = int.MinValue;
        private static Color? _lastChromeBackground;

        public static int NormalizeTheme(int theme)
        {
            if (theme < ThemeSystem || theme > ThemeMax)
            {
                return ThemeSystem;
            }
            return theme;
        }

        public static int GetSavedTheme()
        {
            return NormalizeTheme(SettingHelper.GetValue<int>(SettingHelper.THEME, ThemeSystem));
        }

        public static ElementTheme GetElementTheme(int theme)
        {
            theme = NormalizeTheme(theme);
            switch (theme)
            {
                case ThemeLight:
                    return ElementTheme.Light;
                case ThemeDark:
                case ThemeOneDarkPro:
                case ThemeNord:
                case ThemeDracula:
                case ThemeCatppuccinMocha:
                case ThemeTrueBlack:
                    return ElementTheme.Dark;
                default:
                    return ElementTheme.Default;
            }
        }

        /// <summary>
        /// 标题栏按钮前景：浅色主题用黑，纯黑主题用柔和浅灰，其他深色/自定义用白，跟随系统用系统前景色。
        /// </summary>
        public static Color GetTitleBarButtonForeground(Color systemForeground)
        {
            var theme = GetSavedTheme();
            if (theme == ThemeSystem)
            {
                return systemForeground;
            }
            if (theme == ThemeLight)
            {
                return Colors.Black;
            }
            // 纯黑主题避免标题栏纯白高对比
            if (theme == ThemeTrueBlack)
            {
                return Hex("C2C2C2");
            }
            return Colors.White;
        }

        /// <summary>
        /// 标题栏/窗口 chrome 背景。自定义主题用调色板主背景；浅/深色用固定色；跟随系统返回 null（保持透明）。
        /// </summary>
        public static Color? GetTitleBarBackgroundColor()
        {
            var theme = GetSavedTheme();
            if (theme == ThemeSystem)
            {
                return null;
            }
            if (theme == ThemeLight)
            {
                return Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
            }
            if (theme == ThemeDark)
            {
                return Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
            }
            if (_lastChromeBackground.HasValue)
            {
                return _lastChromeBackground.Value;
            }
            return GetPalette(theme).Background;
        }

        public static bool IsCustomPaletteTheme(int theme)
        {
            theme = NormalizeTheme(theme);
            return theme >= ThemeOneDarkPro;
        }

        /// <summary>
        /// Application.Current.Resources 只能在主视图 UI 线程写入；副窗口（直播间新窗口）会 RPC_E_WRONG_THREAD。
        /// </summary>
        private static bool CanMutateAppResources()
        {
            try
            {
                return CoreApplication.MainView.Dispatcher.HasThreadAccess;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 将当前设置中的主题应用到指定根元素（主 Frame / 新窗口 Frame）。
        /// 副窗口仅设置 RequestedTheme，不改写应用级资源字典。
        /// </summary>
        public static void Apply(FrameworkElement root = null)
        {
            Apply(GetSavedTheme(), root);
        }

        public static void Apply(int theme, FrameworkElement root = null)
        {
            theme = NormalizeTheme(theme);
            var elementTheme = GetElementTheme(theme);

            if (root != null)
            {
                root.RequestedTheme = elementTheme;
            }
            else if (Window.Current?.Content is FrameworkElement frame)
            {
                frame.RequestedTheme = elementTheme;
            }

            // 仅主线程写入 Application.Resources，避免新窗口 0x8001010E
            if (CanMutateAppResources())
            {
                if (IsCustomPaletteTheme(theme))
                {
                    ApplyPalette(GetPalette(theme));
                }
                else if (_lastAppliedTheme != theme || AppliedKeys.Count > 0)
                {
                    ClearCustomPalette();
                    _lastChromeBackground = null;
                    // 系统浅/深色也去掉边框，保持观感一致
                    ApplyBorderlessChrome();
                }
                else if (theme == ThemeLight || theme == ThemeDark)
                {
                    ApplyBorderlessChrome();
                }

                _lastAppliedTheme = theme;
            }
            else if (root != null && IsCustomPaletteTheme(theme))
            {
                // 副窗口：把核心调色板写到本地 Resources，避免只吃系统 Dark 默认色
                ApplyChromePaletteToDictionary(root.Resources, GetPalette(theme));
            }

            try
            {
                global::AllLive.UWP.App.SetTitleBar();
            }
            catch
            {
                // 标题栏在部分窗口上下文中可能不可用
            }
        }

        /// <summary>
        /// 给任意 FrameworkElement（弹窗、Toast、Page）同步 RequestedTheme。
        /// </summary>
        public static void ApplyElementTheme(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }
            element.RequestedTheme = GetElementTheme(GetSavedTheme());
        }

        /// <summary>
        /// 创建并套用当前主题的 ContentDialog（替代系统 MessageDialog，便于跟主题色）。
        /// </summary>
        public static ContentDialog CreateContentDialog()
        {
            var dialog = new ContentDialog();
            ApplyElementTheme(dialog);
            return dialog;
        }

        /// <summary>
        /// 副窗口 / Popup 本地资源：写入页面背景、表面、文字、强调色等核心键。
        /// </summary>
        private static void ApplyChromePaletteToDictionary(ResourceDictionary dict, ThemePalette palette)
        {
            if (dict == null || palette == null)
            {
                return;
            }

            void SetBrush(string key, Color color)
            {
                var brush = new SolidColorBrush(color);
                if (dict.ContainsKey(key))
                {
                    dict[key] = brush;
                }
                else
                {
                    dict.Add(key, brush);
                }
            }

            void SetColor(string key, Color color)
            {
                if (dict.ContainsKey(key))
                {
                    dict[key] = color;
                }
                else
                {
                    dict.Add(key, color);
                }
            }

            var noBorder = Colors.Transparent;
            SetColor("SystemAccentColor", palette.Accent);
            SetColor("TopPaneBackground", palette.Background);
            SetBrush("ApplicationPageBackgroundThemeBrush", palette.Background);
            SetBrush("LayerFillColorDefaultBrush", palette.Background);
            SetBrush("LayerFillColorAltBrush", palette.SurfaceAlt);
            SetBrush("SolidBackgroundFillColorBaseBrush", palette.Background);
            SetBrush("SolidBackgroundFillColorSecondaryBrush", palette.Background);
            SetBrush("CardBackgroundFillColorDefaultBrush", palette.Surface);
            SetBrush("CardStrokeColorDefaultBrush", noBorder);
            SetBrush("CardStrokeColorDefaultSolidBrush", noBorder);
            SetBrush("DividerStrokeColorDefaultBrush", noBorder);
            SetBrush("ControlStrokeColorDefaultBrush", noBorder);
            SetBrush("TextFillColorPrimaryBrush", palette.Foreground);
            SetBrush("TextFillColorSecondaryBrush", palette.ForegroundSecondary);
            SetBrush("TextFillColorTertiaryBrush", palette.ForegroundTertiary);
            SetBrush("SystemControlForegroundBaseHighBrush", palette.Foreground);
            SetBrush("SystemControlForegroundBaseMediumBrush", palette.ForegroundSecondary);
            SetBrush("SystemControlForegroundBaseMediumHighBrush", palette.Foreground);
            SetBrush("SystemControlHighlightAccentBrush", palette.Accent);
            SetBrush("SystemControlBackgroundAccentBrush", palette.Accent);
            SetBrush("ButtonBackground", palette.Control);
            SetBrush("ButtonForeground", palette.Foreground);
            SetBrush("ButtonBorderBrush", noBorder);
            SetBrush("TextControlBackground", palette.Control);
            SetBrush("TextControlForeground", palette.Foreground);
            SetBrush("TextControlBorderBrush", noBorder);
            SetBrush("ContentDialogBackground", palette.Surface);
            SetBrush("ContentDialogForeground", palette.Foreground);
            SetBrush("ContentDialogBorderBrush", noBorder);
            SetBrush("ContentDialogSmokeFill", Color.FromArgb(0x99, 0, 0, 0));
            SetBrush("FlyoutPresenterBackground", palette.Surface);
            SetBrush("MenuFlyoutPresenterBackground", palette.Surface);
            SetBrush("PivotHeaderForegroundSelectedBrush", palette.Foreground);
            SetBrush("PivotHeaderForegroundUnselectedBrush", palette.ForegroundSecondary);
            SetBrush("DefaultTextForegroundThemeBrush", palette.Foreground);
        }

        /// <summary>
        /// 仅去掉边框（用于跟随系统/浅色/深色），不替换整套调色板。
        /// </summary>
        private static void ApplyBorderlessChrome()
        {
            var transparent = Colors.Transparent;
            void Brush(string key) => SetBrushOnly(key, transparent);

            Brush("CardStrokeColorDefaultBrush");
            Brush("CardStrokeColorDefaultSolidBrush");
            Brush("DividerStrokeColorDefaultBrush");
            Brush("ControlStrokeColorDefaultBrush");
            Brush("ControlStrokeColorSecondaryBrush");
            Brush("ControlStrongStrokeColorDefaultBrush");
            Brush("SurfaceStrokeColorDefaultBrush");
            Brush("SurfaceStrokeColorFlyoutBrush");
            Brush("NavigationViewContentGridBorderBrush");
            Brush("FlyoutBorderThemeBrush");
            Brush("MenuFlyoutPresenterBorderBrush");
            Brush("ContentDialogBorderBrush");
            Brush("ButtonBorderBrush");
            Brush("ButtonBorderBrushPointerOver");
            Brush("ButtonBorderBrushPressed");
            Brush("ButtonBorderBrushDisabled");
            Brush("TextControlBorderBrush");
            Brush("TextControlBorderBrushPointerOver");
            Brush("TextControlBorderBrushDisabled");
            Brush("ComboBoxBorderBrush");
            Brush("ComboBoxBorderBrushPointerOver");
            Brush("ComboBoxBorderBrushPressed");
            Brush("ComboBoxBorderBrushDisabled");
            Brush("ComboBoxDropDownBorderBrush");
        }

        private static void SetBrushOnly(string key, Color color)
        {
            var appResources = GetAppResources();
            var darkDict = GetOrCreateDarkThemeDictionary();
            WriteResource(appResources, OriginalAppValues, key, new SolidColorBrush(color));
            WriteResource(darkDict, OriginalDarkThemeValues, key, new SolidColorBrush(color));
            AppliedKeys.Add(key);
        }

        private static ThemePalette GetPalette(int theme)
        {
            switch (theme)
            {
                case ThemeOneDarkPro:
                    return CreateOneDarkPro();
                case ThemeNord:
                    return CreateNord();
                case ThemeDracula:
                    return CreateDracula();
                case ThemeCatppuccinMocha:
                    return CreateCatppuccinMocha();
                case ThemeTrueBlack:
                    return CreateTrueBlack();
                default:
                    return CreateOneDarkPro();
            }
        }

        private static ResourceDictionary GetAppResources()
        {
            return Application.Current?.Resources;
        }

        private static ResourceDictionary GetOrCreateDarkThemeDictionary()
        {
            var appResources = GetAppResources();
            if (appResources?.ThemeDictionaries == null)
            {
                return null;
            }

            if (appResources.ThemeDictionaries.ContainsKey("Dark")
                && appResources.ThemeDictionaries["Dark"] is ResourceDictionary existing)
            {
                return existing;
            }

            var created = new ResourceDictionary();
            appResources.ThemeDictionaries["Dark"] = created;
            return created;
        }

        private static void CaptureOriginal(ResourceDictionary dict, Dictionary<string, object> store, string key)
        {
            if (dict == null || store.ContainsKey(key))
            {
                return;
            }
            store[key] = dict.ContainsKey(key) ? dict[key] : null;
        }

        private static void WriteResource(ResourceDictionary dict, Dictionary<string, object> originals, string key, object value)
        {
            if (dict == null)
            {
                return;
            }
            CaptureOriginal(dict, originals, key);
            if (dict.ContainsKey(key))
            {
                dict[key] = value;
            }
            else
            {
                dict.Add(key, value);
            }
        }

        private static void RestoreResource(ResourceDictionary dict, Dictionary<string, object> originals, string key)
        {
            if (dict == null || !originals.TryGetValue(key, out var original))
            {
                return;
            }

            if (original == null)
            {
                if (dict.ContainsKey(key))
                {
                    dict.Remove(key);
                }
            }
            else if (dict.ContainsKey(key))
            {
                dict[key] = original;
            }
            else
            {
                dict.Add(key, original);
            }
        }

        private static void ClearCustomPalette()
        {
            if (AppliedKeys.Count == 0
                && OriginalAppValues.Count == 0
                && OriginalDarkThemeValues.Count == 0)
            {
                return;
            }

            var appResources = GetAppResources();
            var darkDict = GetOrCreateDarkThemeDictionary();
            foreach (var key in AppliedKeys)
            {
                RestoreResource(appResources, OriginalAppValues, key);
                RestoreResource(darkDict, OriginalDarkThemeValues, key);
            }

            AppliedKeys.Clear();
            OriginalAppValues.Clear();
            OriginalDarkThemeValues.Clear();
        }

        private static void ApplyPalette(ThemePalette palette)
        {
            var appResources = GetAppResources();
            if (appResources == null || palette == null)
            {
                return;
            }

            // 先清旧键，再写入，避免切换主题残留
            ClearCustomPalette();

            var darkDict = GetOrCreateDarkThemeDictionary();
            _lastChromeBackground = palette.Background;

            void SetColor(string key, Color color)
            {
                WriteResource(appResources, OriginalAppValues, key, color);
                WriteResource(darkDict, OriginalDarkThemeValues, key, color);
                AppliedKeys.Add(key);
            }

            void SetBrush(string key, Color color)
            {
                // 每个字典使用独立 brush 实例，避免共享导致异常
                WriteResource(appResources, OriginalAppValues, key, new SolidColorBrush(color));
                WriteResource(darkDict, OriginalDarkThemeValues, key, new SolidColorBrush(color));
                AppliedKeys.Add(key);
            }

            // 边框统一透明（含控件描边）
            var noBorder = Colors.Transparent;

            // —— 系统强调色 ——
            SetColor("SystemAccentColor", palette.Accent);
            SetColor("SystemAccentColorLight1", palette.AccentLight);
            SetColor("SystemAccentColorLight2", palette.AccentLight);
            SetColor("SystemAccentColorLight3", palette.AccentLight);
            SetColor("SystemAccentColorDark1", palette.AccentDark);
            SetColor("SystemAccentColorDark2", palette.AccentDark);
            SetColor("SystemAccentColorDark3", palette.AccentDark);

            // —— 页面 / 表面（侧栏与主背景同色） ——
            SetBrush("ApplicationPageBackgroundThemeBrush", palette.Background);
            SetBrush("LayerFillColorDefaultBrush", palette.Background);
            SetBrush("LayerFillColorAltBrush", palette.SurfaceAlt);
            SetBrush("LayerOnAcrylicFillColorDefaultBrush", palette.Background);
            SetBrush("SolidBackgroundFillColorBaseBrush", palette.Background);
            SetBrush("SolidBackgroundFillColorSecondaryBrush", palette.Background);
            SetBrush("SolidBackgroundFillColorTertiaryBrush", palette.SurfaceAlt);
            SetBrush("SolidBackgroundFillColorQuarternaryBrush", palette.SurfaceAlt);
            SetBrush("CardBackgroundFillColorDefaultBrush", palette.Surface);
            SetBrush("CardBackgroundFillColorSecondaryBrush", palette.SurfaceAlt);
            SetBrush("SmokeFillColorDefaultBrush", Color.FromArgb(0x66, 0, 0, 0));

            // 应用内自定义键：顶栏/侧栏与主背景一致
            SetColor("TopPaneBackground", palette.Background);

            // —— 文字 ——
            SetBrush("SystemControlForegroundBaseHighBrush", palette.Foreground);
            SetBrush("SystemControlForegroundBaseMediumBrush", palette.ForegroundSecondary);
            SetBrush("SystemControlForegroundBaseMediumHighBrush", palette.Foreground);
            SetBrush("SystemControlForegroundBaseMediumLowBrush", palette.ForegroundSecondary);
            SetBrush("SystemControlForegroundBaseLowBrush", palette.ForegroundTertiary);
            SetBrush("SystemControlForegroundChromeGrayBrush", palette.ForegroundSecondary);
            SetBrush("SystemControlForegroundChromeDisabledLowBrush", palette.ForegroundTertiary);
            SetBrush("SystemControlForegroundChromeDisabledHighBrush", palette.ForegroundSecondary);
            SetBrush("SystemControlForegroundChromeBlackHighBrush", palette.Foreground);
            SetBrush("SystemControlForegroundChromeWhiteBrush", palette.Background);
            SetBrush("SystemControlForegroundTransparentBrush", Colors.Transparent);
            SetBrush("SystemControlForegroundAccentBrush", palette.Accent);

            SetBrush("TextFillColorPrimaryBrush", palette.Foreground);
            SetBrush("TextFillColorSecondaryBrush", palette.ForegroundSecondary);
            SetBrush("TextFillColorTertiaryBrush", palette.ForegroundTertiary);
            SetBrush("TextFillColorDisabledBrush", palette.ForegroundTertiary);
            SetBrush("TextFillColorInverseBrush", palette.Background);

            // —— 边框 / 分割（全部透明） ——
            SetBrush("CardStrokeColorDefaultBrush", noBorder);
            SetBrush("CardStrokeColorDefaultSolidBrush", noBorder);
            SetBrush("DividerStrokeColorDefaultBrush", noBorder);
            SetBrush("ControlStrokeColorDefaultBrush", noBorder);
            SetBrush("ControlStrokeColorSecondaryBrush", noBorder);
            SetBrush("ControlStrokeColorOnAccentDefaultBrush", noBorder);
            SetBrush("ControlStrongStrokeColorDefaultBrush", noBorder);
            SetBrush("SurfaceStrokeColorDefaultBrush", noBorder);
            SetBrush("SurfaceStrokeColorFlyoutBrush", noBorder);
            SetBrush("FocusStrokeColorOuterBrush", palette.Accent);
            SetBrush("FocusStrokeColorInnerBrush", palette.Background);

            // —— 系统控件背景 ——
            SetBrush("SystemControlBackgroundAccentBrush", palette.Accent);
            SetBrush("SystemControlBackgroundBaseLowBrush", palette.SurfaceAlt);
            SetBrush("SystemControlBackgroundBaseMediumLowBrush", palette.Surface);
            SetBrush("SystemControlBackgroundBaseMediumBrush", palette.SurfaceAlt);
            SetBrush("SystemControlBackgroundBaseMediumHighBrush", palette.SurfaceAlt);
            SetBrush("SystemControlBackgroundBaseHighBrush", palette.Surface);
            SetBrush("SystemControlBackgroundChromeMediumBrush", palette.Surface);
            SetBrush("SystemControlBackgroundChromeMediumLowBrush", palette.SurfaceAlt);
            SetBrush("SystemControlBackgroundChromeLowBrush", palette.Background);
            SetBrush("SystemControlBackgroundChromeHighBrush", palette.SurfaceAlt);
            SetBrush("SystemControlBackgroundChromeWhiteBrush", palette.Foreground);
            SetBrush("SystemControlBackgroundChromeBlackLowBrush", palette.Background);
            SetBrush("SystemControlBackgroundChromeBlackMediumLowBrush", palette.Surface);
            SetBrush("SystemControlBackgroundTransparentBrush", Colors.Transparent);
            SetBrush("SystemControlBackgroundListLowBrush", palette.Hover);
            SetBrush("SystemControlBackgroundListMediumBrush", palette.Pressed);
            SetBrush("SystemControlHighlightListAccentLowBrush", WithAlpha(palette.Accent, 0x33));
            SetBrush("SystemControlHighlightListAccentMediumBrush", WithAlpha(palette.Accent, 0x66));
            SetBrush("SystemControlHighlightListAccentHighBrush", WithAlpha(palette.Accent, 0x99));
            SetBrush("SystemControlHighlightAccentBrush", palette.Accent);
            SetBrush("SystemControlHighlightAltAccentBrush", palette.Accent);
            SetBrush("SystemControlHighlightListLowBrush", palette.Hover);
            SetBrush("SystemControlHighlightListMediumBrush", palette.Pressed);
            SetBrush("SystemControlHighlightChromeAltLowBrush", palette.ForegroundSecondary);
            SetBrush("SystemControlDisabledBaseLowBrush", palette.SurfaceAlt);
            SetBrush("SystemControlDisabledBaseMediumLowBrush", palette.ForegroundTertiary);
            SetBrush("SystemControlDisabledChromeDisabledLowBrush", palette.ForegroundTertiary);
            SetBrush("SystemControlDisabledChromeDisabledHighBrush", palette.SurfaceAlt);
            SetBrush("SystemControlDisabledTransparentBrush", Colors.Transparent);
            SetBrush("SystemControlFocusVisualPrimaryBrush", palette.Accent);
            SetBrush("SystemControlFocusVisualSecondaryBrush", palette.Background);
            SetBrush("SystemControlTransparentBrush", Colors.Transparent);
            SetBrush("SystemControlPageBackgroundChromeLowBrush", palette.Background);
            SetBrush("SystemControlPageBackgroundMediumHighBrush", palette.Background);
            SetBrush("SystemControlPageBackgroundBaseLowBrush", palette.Surface);
            SetBrush("SystemControlPageBackgroundBaseMediumBrush", palette.SurfaceAlt);
            SetBrush("SystemControlPageTextBaseHighBrush", palette.Foreground);
            SetBrush("SystemControlPageTextBaseMediumBrush", palette.ForegroundSecondary);
            SetBrush("SystemControlPageTextChromeBlackMediumLowBrush", palette.ForegroundSecondary);

            // —— 按钮 ——
            SetBrush("ButtonBackground", palette.Control);
            SetBrush("ButtonBackgroundPointerOver", palette.Hover);
            SetBrush("ButtonBackgroundPressed", palette.Pressed);
            SetBrush("ButtonBackgroundDisabled", palette.SurfaceAlt);
            SetBrush("ButtonForeground", palette.Foreground);
            SetBrush("ButtonForegroundPointerOver", palette.Foreground);
            SetBrush("ButtonForegroundPressed", palette.ForegroundSecondary);
            SetBrush("ButtonForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("ButtonBorderBrush", noBorder);
            SetBrush("ButtonBorderBrushPointerOver", noBorder);
            SetBrush("ButtonBorderBrushPressed", noBorder);
            SetBrush("ButtonBorderBrushDisabled", noBorder);

            SetBrush("AccentButtonBackground", palette.Accent);
            SetBrush("AccentButtonBackgroundPointerOver", palette.AccentLight);
            SetBrush("AccentButtonBackgroundPressed", palette.AccentDark);
            SetBrush("AccentButtonBackgroundDisabled", palette.SurfaceAlt);
            SetBrush("AccentButtonForeground", palette.OnAccent);
            SetBrush("AccentButtonForegroundPointerOver", palette.OnAccent);
            SetBrush("AccentButtonForegroundPressed", palette.OnAccent);
            SetBrush("AccentButtonForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("AccentButtonBorderBrush", noBorder);
            SetBrush("AccentButtonBorderBrushPointerOver", noBorder);
            SetBrush("AccentButtonBorderBrushPressed", noBorder);
            SetBrush("AccentButtonBorderBrushDisabled", noBorder);

            // —— 文本框 / 密码框 ——
            SetBrush("TextControlBackground", palette.Control);
            SetBrush("TextControlBackgroundPointerOver", palette.Hover);
            SetBrush("TextControlBackgroundFocused", palette.Control);
            SetBrush("TextControlBackgroundDisabled", palette.SurfaceAlt);
            SetBrush("TextControlForeground", palette.Foreground);
            SetBrush("TextControlForegroundPointerOver", palette.Foreground);
            SetBrush("TextControlForegroundFocused", palette.Foreground);
            SetBrush("TextControlForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("TextControlPlaceholderForeground", palette.ForegroundTertiary);
            SetBrush("TextControlPlaceholderForegroundPointerOver", palette.ForegroundSecondary);
            SetBrush("TextControlPlaceholderForegroundFocused", palette.ForegroundSecondary);
            SetBrush("TextControlPlaceholderForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("TextControlBorderBrush", noBorder);
            SetBrush("TextControlBorderBrushPointerOver", noBorder);
            SetBrush("TextControlBorderBrushFocused", palette.Accent);
            SetBrush("TextControlBorderBrushDisabled", noBorder);
            SetBrush("TextControlButtonBackground", Colors.Transparent);
            SetBrush("TextControlButtonBackgroundPointerOver", palette.Hover);
            SetBrush("TextControlButtonBackgroundPressed", palette.Pressed);
            SetBrush("TextControlButtonForeground", palette.ForegroundSecondary);
            SetBrush("TextControlButtonForegroundPointerOver", palette.Foreground);
            SetBrush("TextControlButtonForegroundPressed", palette.Foreground);
            SetBrush("TextControlSelectionHighlightColor", WithAlpha(palette.Accent, 0x99));

            // —— ComboBox ——
            SetBrush("ComboBoxBackground", palette.Control);
            SetBrush("ComboBoxBackgroundPointerOver", palette.Hover);
            SetBrush("ComboBoxBackgroundPressed", palette.Pressed);
            SetBrush("ComboBoxBackgroundDisabled", palette.SurfaceAlt);
            SetBrush("ComboBoxBackgroundFocused", palette.Control);
            SetBrush("ComboBoxForeground", palette.Foreground);
            SetBrush("ComboBoxForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("ComboBoxForegroundFocused", palette.Foreground);
            SetBrush("ComboBoxForegroundPointerOver", palette.Foreground);
            SetBrush("ComboBoxForegroundPressed", palette.Foreground);
            SetBrush("ComboBoxPlaceHolderForeground", palette.ForegroundTertiary);
            SetBrush("ComboBoxPlaceHolderForegroundFocused", palette.ForegroundSecondary);
            SetBrush("ComboBoxBorderBrush", noBorder);
            SetBrush("ComboBoxBorderBrushPointerOver", noBorder);
            SetBrush("ComboBoxBorderBrushPressed", noBorder);
            SetBrush("ComboBoxBorderBrushFocused", palette.Accent);
            SetBrush("ComboBoxBorderBrushDisabled", noBorder);
            SetBrush("ComboBoxDropDownBackground", palette.Surface);
            SetBrush("ComboBoxDropDownBorderBrush", noBorder);
            SetBrush("ComboBoxItemForeground", palette.Foreground);
            SetBrush("ComboBoxItemForegroundSelected", palette.Foreground);
            SetBrush("ComboBoxItemForegroundPointerOver", palette.Foreground);
            SetBrush("ComboBoxItemForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("ComboBoxItemBackgroundSelected", WithAlpha(palette.Accent, 0x44));
            SetBrush("ComboBoxItemBackgroundPointerOver", palette.Hover);
            SetBrush("ComboBoxItemBackgroundPressed", palette.Pressed);

            // —— ToggleSwitch ——
            SetBrush("ToggleSwitchFillOn", palette.Accent);
            SetBrush("ToggleSwitchFillOnPointerOver", palette.AccentLight);
            SetBrush("ToggleSwitchFillOnPressed", palette.AccentDark);
            SetBrush("ToggleSwitchFillOnDisabled", palette.SurfaceAlt);
            SetBrush("ToggleSwitchFillOff", palette.Control);
            SetBrush("ToggleSwitchFillOffPointerOver", palette.Hover);
            SetBrush("ToggleSwitchFillOffPressed", palette.Pressed);
            SetBrush("ToggleSwitchFillOffDisabled", palette.SurfaceAlt);
            SetBrush("ToggleSwitchStrokeOn", palette.Accent);
            SetBrush("ToggleSwitchStrokeOnPointerOver", palette.AccentLight);
            SetBrush("ToggleSwitchStrokeOnPressed", palette.AccentDark);
            SetBrush("ToggleSwitchStrokeOnDisabled", palette.Border);
            SetBrush("ToggleSwitchStrokeOff", noBorder);
            SetBrush("ToggleSwitchStrokeOffPointerOver", noBorder);
            SetBrush("ToggleSwitchStrokeOffPressed", noBorder);
            SetBrush("ToggleSwitchStrokeOffDisabled", noBorder);
            SetBrush("ToggleSwitchKnobFillOn", palette.OnAccent);
            SetBrush("ToggleSwitchKnobFillOnPointerOver", palette.OnAccent);
            SetBrush("ToggleSwitchKnobFillOnPressed", palette.OnAccent);
            SetBrush("ToggleSwitchKnobFillOnDisabled", palette.ForegroundTertiary);
            SetBrush("ToggleSwitchKnobFillOff", palette.ForegroundSecondary);
            SetBrush("ToggleSwitchKnobFillOffPointerOver", palette.Foreground);
            SetBrush("ToggleSwitchKnobFillOffPressed", palette.Foreground);
            SetBrush("ToggleSwitchKnobFillOffDisabled", palette.ForegroundTertiary);
            SetBrush("ToggleSwitchHeaderForeground", palette.Foreground);
            SetBrush("ToggleSwitchHeaderForegroundDisabled", palette.ForegroundTertiary);

            // —— CheckBox / Radio ——
            SetBrush("CheckBoxForegroundUnchecked", palette.Foreground);
            SetBrush("CheckBoxForegroundChecked", palette.Foreground);
            SetBrush("CheckBoxForegroundIndeterminate", palette.Foreground);
            SetBrush("CheckBoxCheckBackgroundFillUnchecked", palette.Control);
            SetBrush("CheckBoxCheckBackgroundFillChecked", palette.Accent);
            SetBrush("CheckBoxCheckBackgroundFillIndeterminate", palette.Accent);
            SetBrush("CheckBoxCheckBackgroundStrokeUnchecked", noBorder);
            SetBrush("CheckBoxCheckBackgroundStrokeChecked", palette.Accent);
            SetBrush("CheckBoxCheckGlyphForegroundChecked", palette.OnAccent);
            SetBrush("CheckBoxCheckGlyphForegroundIndeterminate", palette.OnAccent);

            // —— ListView / GridView ——
            SetBrush("ListViewItemBackground", Colors.Transparent);
            SetBrush("ListViewItemBackgroundPointerOver", palette.Hover);
            SetBrush("ListViewItemBackgroundPressed", palette.Pressed);
            SetBrush("ListViewItemBackgroundSelected", WithAlpha(palette.Accent, 0x44));
            SetBrush("ListViewItemBackgroundSelectedPointerOver", WithAlpha(palette.Accent, 0x66));
            SetBrush("ListViewItemBackgroundSelectedPressed", WithAlpha(palette.Accent, 0x88));
            SetBrush("ListViewItemForeground", palette.Foreground);
            SetBrush("ListViewItemForegroundPointerOver", palette.Foreground);
            SetBrush("ListViewItemForegroundPressed", palette.Foreground);
            SetBrush("ListViewItemForegroundSelected", palette.Foreground);
            SetBrush("ListViewItemForegroundSelectedPointerOver", palette.Foreground);
            SetBrush("ListViewItemForegroundSelectedPressed", palette.Foreground);
            SetBrush("ListViewItemFocusBorderBrush", palette.Accent);
            SetBrush("ListViewItemPlaceholderBackgroundThemeBrush", palette.SurfaceAlt);
            SetBrush("ListViewItemPlaceholderBackground", palette.SurfaceAlt);

            // —— NavigationView（侧栏与主背景同色） ——
            SetBrush("NavigationViewDefaultPaneBackground", palette.Background);
            SetBrush("NavigationViewExpandedPaneBackground", palette.Background);
            SetBrush("NavigationViewTopPaneBackground", palette.Background);
            SetBrush("NavigationViewContentBackground", palette.Background);
            SetBrush("NavigationViewItemBackground", Colors.Transparent);
            SetBrush("NavigationViewItemBackgroundPointerOver", palette.Hover);
            SetBrush("NavigationViewItemBackgroundPressed", palette.Pressed);
            SetBrush("NavigationViewItemBackgroundSelected", WithAlpha(palette.Accent, 0x33));
            SetBrush("NavigationViewItemBackgroundSelectedPointerOver", WithAlpha(palette.Accent, 0x55));
            SetBrush("NavigationViewItemBackgroundSelectedPressed", WithAlpha(palette.Accent, 0x77));
            SetBrush("NavigationViewItemForeground", palette.Foreground);
            SetBrush("NavigationViewItemForegroundPointerOver", palette.Foreground);
            SetBrush("NavigationViewItemForegroundPressed", palette.Foreground);
            SetBrush("NavigationViewItemForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("NavigationViewItemForegroundSelected", palette.Foreground);
            SetBrush("NavigationViewItemForegroundSelectedPointerOver", palette.Foreground);
            SetBrush("NavigationViewItemForegroundSelectedPressed", palette.Foreground);
            SetBrush("NavigationViewSelectionIndicatorForeground", palette.Accent);
            SetBrush("NavigationViewButtonBackgroundPointerOver", palette.Hover);
            SetBrush("NavigationViewButtonBackgroundPressed", palette.Pressed);
            SetBrush("NavigationViewContentGridBorderBrush", noBorder);
            SetBrush("TopNavigationViewItemForeground", palette.Foreground);
            SetBrush("TopNavigationViewItemForegroundPointerOver", palette.Foreground);
            SetBrush("TopNavigationViewItemForegroundPressed", palette.Foreground);
            SetBrush("TopNavigationViewItemForegroundSelected", palette.Foreground);
            SetBrush("TopNavigationViewItemBackgroundPointerOver", palette.Hover);
            SetBrush("TopNavigationViewItemBackgroundPressed", palette.Pressed);
            SetBrush("TopNavigationViewItemBackgroundSelected", WithAlpha(palette.Accent, 0x33));

            // —— Flyout / Menu / ContentDialog ——
            SetBrush("FlyoutPresenterBackground", palette.Surface);
            SetBrush("FlyoutBorderThemeBrush", noBorder);
            SetBrush("MenuFlyoutPresenterBackground", palette.Surface);
            SetBrush("MenuFlyoutPresenterBorderBrush", noBorder);
            SetBrush("MenuFlyoutItemBackground", Colors.Transparent);
            SetBrush("MenuFlyoutItemBackgroundPointerOver", palette.Hover);
            SetBrush("MenuFlyoutItemBackgroundPressed", palette.Pressed);
            SetBrush("MenuFlyoutItemForeground", palette.Foreground);
            SetBrush("MenuFlyoutItemForegroundPointerOver", palette.Foreground);
            SetBrush("MenuFlyoutItemForegroundPressed", palette.Foreground);
            SetBrush("MenuFlyoutItemForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("ContentDialogBackground", palette.Surface);
            SetBrush("ContentDialogForeground", palette.Foreground);
            SetBrush("ContentDialogBorderBrush", noBorder);
            SetBrush("ContentDialogTopOverlay", palette.SurfaceAlt);
            SetBrush("ContentDialogSmokeFill", Color.FromArgb(0x99, 0, 0, 0));

            // —— ScrollBar / Slider / Progress ——
            SetBrush("ScrollBarThumbBackground", palette.BorderStrong);
            SetBrush("ScrollBarThumbBackgroundPointerOver", palette.ForegroundTertiary);
            SetBrush("ScrollBarThumbBackgroundPressed", palette.ForegroundSecondary);
            SetBrush("SliderTrackFill", palette.Border);
            SetBrush("SliderTrackValueFill", palette.Accent);
            SetBrush("SliderThumbBackground", palette.Accent);
            SetBrush("ProgressBarForeground", palette.Accent);
            SetBrush("ProgressBarBackground", palette.Border);

            // —— AppBar / CommandBar ——
            SetBrush("AppBarBackground", palette.Surface);
            SetBrush("AppBarForeground", palette.Foreground);
            SetBrush("AppBarButtonBackground", Colors.Transparent);
            SetBrush("AppBarButtonBackgroundPointerOver", palette.Hover);
            SetBrush("AppBarButtonBackgroundPressed", palette.Pressed);
            SetBrush("AppBarButtonForeground", palette.Foreground);
            SetBrush("AppBarButtonForegroundPointerOver", palette.Foreground);
            SetBrush("AppBarButtonForegroundPressed", palette.Foreground);
            SetBrush("AppBarButtonForegroundDisabled", palette.ForegroundTertiary);
            SetBrush("CommandBarBackground", palette.Surface);
            SetBrush("CommandBarForeground", palette.Foreground);

            // —— Pivot / TabView-ish ——
            SetBrush("PivotHeaderForegroundSelectedBrush", palette.Foreground);
            SetBrush("PivotHeaderForegroundUnselectedBrush", palette.ForegroundSecondary);
            SetBrush("PivotHeaderForegroundSelectedPointerOverBrush", palette.Foreground);
            SetBrush("PivotHeaderForegroundUnselectedPointerOverBrush", palette.Foreground);
            SetBrush("SystemControlHighlightAltBaseHighBrush", palette.Foreground);
            SetBrush("SystemControlHighlightBaseMediumLowBrush", palette.Hover);
            SetBrush("SystemControlHighlightBaseMediumBrush", palette.Pressed);
            SetBrush("SystemControlHighlightBaseHighBrush", palette.Accent);

            // —— Hyperlink / SystemReveal ——
            SetBrush("SystemControlHyperlinkTextBrush", palette.Accent);
            SetBrush("HyperlinkButtonForeground", palette.Accent);
            SetBrush("HyperlinkButtonForegroundPointerOver", palette.AccentLight);
            SetBrush("HyperlinkButtonForegroundPressed", palette.AccentDark);

            // —— InfoBar / Tip 等通用 ——
            SetBrush("InfoBarInformationalSeverityBackgroundBrush", WithAlpha(palette.Accent, 0x33));
            SetBrush("SystemControlDescriptionTextBaseMediumBrush", palette.ForegroundSecondary);
            SetBrush("DefaultTextForegroundThemeBrush", palette.Foreground);
            SetBrush("SystemColorControlTextColorBrush", palette.Foreground);
            SetBrush("SystemColorWindowColorBrush", palette.Background);
            SetBrush("SystemColorWindowTextColorBrush", palette.Foreground);
            SetBrush("SystemColorHighlightColorBrush", palette.Accent);
            SetBrush("SystemColorHighlightTextColorBrush", palette.OnAccent);
            SetBrush("SystemColorButtonFaceColorBrush", palette.Control);
            SetBrush("SystemColorButtonTextColorBrush", palette.Foreground);
            SetBrush("SystemColorGrayTextColorBrush", palette.ForegroundTertiary);
        }

        private static Color WithAlpha(Color color, byte alpha)
        {
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static Color Hex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return Colors.Transparent;
            }
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                return Color.FromArgb(
                    0xFF,
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16));
            }
            if (hex.Length == 8)
            {
                return Color.FromArgb(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16),
                    Convert.ToByte(hex.Substring(6, 2), 16));
            }
            return Colors.Transparent;
        }

        // —— 调色板定义（官方/社区常用色） ——

        private static ThemePalette CreateOneDarkPro()
        {
            // Atom One Dark / One Dark Pro
            return new ThemePalette
            {
                Background = Hex("282C34"),
                Surface = Hex("21252B"),
                SurfaceAlt = Hex("2C313A"),
                Control = Hex("333842"),
                Hover = Hex("3A3F4B"),
                Pressed = Hex("2C313C"),
                Border = Hex("3E4451"),
                BorderStrong = Hex("5C6370"),
                Foreground = Hex("ABB2BF"),
                ForegroundSecondary = Hex("9DA5B4"),
                ForegroundTertiary = Hex("5C6370"),
                Accent = Hex("61AFEF"),
                AccentLight = Hex("7EC0F7"),
                AccentDark = Hex("4B93D1"),
                OnAccent = Hex("FFFFFF"),
            };
        }

        private static ThemePalette CreateNord()
        {
            // Nord polar night + frost
            return new ThemePalette
            {
                Background = Hex("2E3440"),
                Surface = Hex("3B4252"),
                SurfaceAlt = Hex("434C5E"),
                Control = Hex("3B4252"),
                Hover = Hex("434C5E"),
                Pressed = Hex("4C566A"),
                Border = Hex("4C566A"),
                BorderStrong = Hex("D8DEE9"),
                Foreground = Hex("ECEFF4"),
                ForegroundSecondary = Hex("E5E9F0"),
                ForegroundTertiary = Hex("D8DEE9"),
                Accent = Hex("88C0D0"),
                AccentLight = Hex("8FBCBB"),
                AccentDark = Hex("5E81AC"),
                OnAccent = Hex("2E3440"),
            };
        }

        private static ThemePalette CreateDracula()
        {
            return new ThemePalette
            {
                Background = Hex("282A36"),
                Surface = Hex("21222C"),
                SurfaceAlt = Hex("343746"),
                Control = Hex("44475A"),
                Hover = Hex("44475A"),
                Pressed = Hex("6272A4"),
                Border = Hex("44475A"),
                BorderStrong = Hex("6272A4"),
                Foreground = Hex("F8F8F2"),
                ForegroundSecondary = Hex("E2E2DC"),
                ForegroundTertiary = Hex("6272A4"),
                Accent = Hex("BD93F9"),
                AccentLight = Hex("D6B8FF"),
                AccentDark = Hex("9A75D1"),
                OnAccent = Hex("282A36"),
            };
        }

        private static ThemePalette CreateCatppuccinMocha()
        {
            // Catppuccin Mocha
            return new ThemePalette
            {
                Background = Hex("1E1E2E"),
                Surface = Hex("181825"),
                SurfaceAlt = Hex("313244"),
                Control = Hex("313244"),
                Hover = Hex("45475A"),
                Pressed = Hex("585B70"),
                Border = Hex("45475A"),
                BorderStrong = Hex("585B70"),
                Foreground = Hex("CDD6F4"),
                ForegroundSecondary = Hex("BAC2DE"),
                ForegroundTertiary = Hex("6C7086"),
                Accent = Hex("89B4FA"),
                AccentLight = Hex("B4BEFE"),
                AccentDark = Hex("74C7EC"),
                OnAccent = Hex("1E1E2E"),
            };
        }

        private static ThemePalette CreateTrueBlack()
        {
            // 纯黑 OLED 风格：背景 #000000；正文用柔和浅灰（非纯白），次级再降一档
            // 主文字 #C2C2C2 在纯黑上约 9.5:1，可读且不刺眼
            return new ThemePalette
            {
                Background = Hex("000000"),
                Surface = Hex("000000"),
                SurfaceAlt = Hex("141414"),
                Control = Hex("1A1A1A"),
                Hover = Hex("242424"),
                Pressed = Hex("2E2E2E"),
                Border = Hex("2A2A2A"),
                BorderStrong = Hex("3A3A3A"),
                Foreground = Hex("C2C2C2"),
                ForegroundSecondary = Hex("9A9A9A"),
                ForegroundTertiary = Hex("6E6E6E"),
                Accent = Hex("8A9BB0"),
                AccentLight = Hex("A8B6C8"),
                AccentDark = Hex("6E7F94"),
                OnAccent = Hex("000000"),
            };
        }

        private sealed class ThemePalette
        {
            public Color Background;
            public Color Surface;
            public Color SurfaceAlt;
            public Color Control;
            public Color Hover;
            public Color Pressed;
            public Color Border;
            public Color BorderStrong;
            public Color Foreground;
            public Color ForegroundSecondary;
            public Color ForegroundTertiary;
            public Color Accent;
            public Color AccentLight;
            public Color AccentDark;
            public Color OnAccent;
        }
    }
}
