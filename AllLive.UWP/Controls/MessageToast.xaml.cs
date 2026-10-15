using AllLive.UWP.Helper;
using Microsoft.Toolkit.Uwp.UI.Animations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

//https://go.microsoft.com/fwlink/?LinkId=234236 上介绍了“用户控件”项模板

namespace AllLive.UWP.Controls
{
    public sealed partial class MessageToast : UserControl
    {
        private Popup m_Popup;

        private string m_TextBlockContent = "";
        private TimeSpan m_ShowTime;
        private bool m_Persistent;
        private bool m_IsClosed;
        public MessageToast()
        {
            this.InitializeComponent();
            // Popup 默认易落在浅色；Apply 会设 RequestedTheme，副线程场景还会写入本地调色板
            ThemeHelper.Apply(this);
            m_Popup = new Popup();
            ThemeHelper.ApplyElementTheme(m_Popup);
            this.Width = Window.Current.Bounds.Width;
            this.Height = Window.Current.Bounds.Height;
            m_Popup.Child = this;
            this.Loaded += NotifyPopup_Loaded;
            this.Unloaded += NotifyPopup_Unloaded;
        }

        public MessageToast(string content, TimeSpan showTime) : this()
        {
            if (m_TextBlockContent == null)
            {
                m_TextBlockContent = "";
            }
            this.m_TextBlockContent = content;
            this.m_ShowTime = showTime;
        }
        public MessageToast(string content, TimeSpan showTime, List<MyUICommand> commands) : this()
        {
            if (m_TextBlockContent == null)
            {
                m_TextBlockContent = "";
            }
            this.m_TextBlockContent = content;
            this.m_ShowTime = showTime;
            foreach (var item in commands)
            {
                HyperlinkButton button = new HyperlinkButton()
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = new TextBlock() { Text = item.Label }
                };
                button.Click += new RoutedEventHandler((sender, e) => {
                    item.Invoked?.Invoke(this, item);
                });
                btns.Children.Add(button);
            }
        }


        // 常驻模式：不自动关闭，调用方可 SetText 更新文字、Close 主动关闭（用于风控暂停倒计时）。
        public MessageToast(string content, bool persistent) : this()
        {
            if (m_TextBlockContent == null)
            {
                m_TextBlockContent = "";
            }
            this.m_TextBlockContent = content;
            this.m_ShowTime = TimeSpan.Zero;
            this.m_Persistent = persistent;
        }

        public void Show()
        {
            this.m_Popup.IsOpen = true;

        }

        public bool IsClosed
        {
            get { return m_IsClosed; }
        }

        // 动态更新文字（仅 UI 线程调用）。
        public void SetText(string text)
        {
            m_TextBlockContent = text ?? "";
            if (!m_IsClosed)
            {
                this.tbNotify.Text = m_TextBlockContent;
            }
        }

        public async void Close()
        {
            if (m_IsClosed)
            {
                return;
            }
            m_IsClosed = true;
            await AnimationBuilder.Create().Offset(to: new Vector2(0, (float)border.ActualHeight), duration: TimeSpan.FromMilliseconds(200)).StartAsync(this);

           // await this.Offset(offsetX: 0, offsetY: (float)border.ActualHeight, duration: 200, delay: 0, easingType: EasingType.Default).StartAsync();
            this.m_Popup.IsOpen = false;
        }
        private async void NotifyPopup_Loaded(object sender, RoutedEventArgs e)
        {
            if (m_TextBlockContent == null)
            {
                m_TextBlockContent = "";
            }
            this.tbNotify.Text = m_TextBlockContent;
            Window.Current.SizeChanged += Current_SizeChanged;
            await AnimationBuilder.Create().Offset(to: new Vector2(0, -72), duration: TimeSpan.FromMilliseconds(200)).StartAsync(this);
            //await this.Offset(offsetX: 0, offsetY: -72, duration: 200, delay: 0, easingType: EasingType.Default).StartAsync();
            if (m_Persistent)
            {
                // 常驻模式：保持显示，等待调用方 SetText / Close。
                return;
            }
            await AnimationBuilder.Create().Offset(to: new Vector2(0, (float)border.ActualHeight), duration: TimeSpan.FromMilliseconds(200), delay: TimeSpan.FromMilliseconds(m_ShowTime.TotalMilliseconds)).StartAsync(this);
            //await this.Offset(offsetX: 0, offsetY: (float)border.ActualHeight, duration: 200, delay: m_ShowTime.TotalMilliseconds, easingType: EasingType.Default).StartAsync();
            if (!m_IsClosed)
            {
                m_IsClosed = true;
                this.m_Popup.IsOpen = false;
            }
        }


        private void Current_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            this.Width = e.Size.Width;
            this.Height = e.Size.Height;
        }

        private void NotifyPopup_Unloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Current_SizeChanged;
        }


    }
    public class MyUICommand
    {
        public MyUICommand(string lable)
        {
            Label = lable;
        }
        public MyUICommand(string lable, EventHandler<MyUICommand> invoked)
        {
            Label = lable;
            Invoked = invoked;
        }
        public object Id { get; set; }
        public EventHandler<MyUICommand> Invoked { get; set; }
        public string Label { get; set; }


    }
}
