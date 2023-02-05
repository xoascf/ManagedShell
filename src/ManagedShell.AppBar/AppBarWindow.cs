﻿using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace ManagedShell.AppBar
{
    public class AppBarWindow : Window, INotifyPropertyChanged
    {
        protected readonly AppBarManager _appBarManager;
        protected readonly ExplorerHelper _explorerHelper;
        protected readonly FullScreenHelper _fullScreenHelper;
        public AppBarScreen Screen;
        public double DpiScale = 1.0;
        protected bool ProcessScreenChanges = true;

        // Window properties
        private WindowInteropHelper helper;
        private bool IsRaising;
        public IntPtr Handle;
        public bool AllowClose;
        public bool IsClosing;
        public bool IsOpening = true;
        protected double DesiredHeight;
        protected double DesiredWidth;
        private bool EnableBlur;

        // AppBar properties
        private int AppBarMessageId = -1;

        private AppBarEdge _appBarEdge;
        public AppBarEdge AppBarEdge
        {
            get
            {
                return _appBarEdge;
            }
            set
            {
                _appBarEdge = value;
                OnPropertyChanged();
                OnPropertyChanged("Orientation");
            }
        }
        protected internal bool EnableAppBar = true;
        protected internal bool RequiresScreenEdge;

        public Orientation Orientation
        {
            get => (AppBarEdge == AppBarEdge.Left || AppBarEdge == AppBarEdge.Right) ? Orientation.Vertical : Orientation.Horizontal;
        }

        public AppBarWindow(AppBarManager appBarManager, ExplorerHelper explorerHelper, FullScreenHelper fullScreenHelper, AppBarScreen screen, AppBarEdge edge, double size)
        {
            _explorerHelper = explorerHelper;
            _fullScreenHelper = fullScreenHelper;
            _appBarManager = appBarManager;

            Closing += OnClosing;
            SourceInitialized += OnSourceInitialized;

            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Title = "";
            Topmost = true;
            UseLayoutRounding = true;
            WindowStyle = WindowStyle.None;

            Screen = screen;
            AppBarEdge = edge;

            if (Orientation == Orientation.Vertical)
            {
                DesiredWidth = size;
            }
            else
            {
                DesiredHeight = size;
            }
        }

        #region Events
        protected virtual void OnSourceInitialized(object sender, EventArgs e)
        {
            // set up helper and get handle
            helper = new WindowInteropHelper(this);
            Handle = helper.Handle;

            // set up window procedure
            HwndSource source = HwndSource.FromHwnd(Handle);
            source.AddHook(WndProc);

            // set initial DPI. We do it here so that we get the correct value when DPI has changed since initial user logon to the system.
            if (Screen.Primary)
            {
                DpiHelper.DpiScale = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice.M11;
            }

            // use system DPI initially; when we set position we will get WM_DPICHANGED and set it correctly
            DpiScale = DpiHelper.DpiScale;

            SetPosition();

            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                // set position again, on a delay, in case one display has a different DPI. for some reason the system overrides us if we don't wait
                DelaySetPosition();
            }

            RegisterAppBar();

            // hide from alt-tab etc
            WindowHelper.HideWindowFromTasks(Handle);

            // register for full-screen notifications
            _fullScreenHelper.FullScreenApps.CollectionChanged += FullScreenApps_CollectionChanged;

            IsOpening = false;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            IsClosing = true;

            CustomClosing();

            if (AllowClose)
            {
                UnregisterAppBar();

                // unregister full-screen notifications
                _fullScreenHelper.FullScreenApps.CollectionChanged -= FullScreenApps_CollectionChanged;
            }
            else
            {
                IsClosing = false;
                e.Cancel = true;
            }
        }

        private void FullScreenApps_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            bool found = false;

            foreach (FullScreenApp app in _fullScreenHelper.FullScreenApps)
            {
                if (app.screen.DeviceName == Screen.DeviceName || app.screen.IsVirtualScreen)
                {
                    // we need to not be on top now
                    found = true;
                    break;
                }
            }

            if (found && Topmost)
            {
                setFullScreenMode(true);
            }
            else if (!found && !Topmost)
            {
                setFullScreenMode(false);
            }
        }

        protected virtual IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == AppBarMessageId && AppBarMessageId != -1)
            {
                switch ((NativeMethods.AppBarNotifications)wParam.ToInt32())
                {
                    case NativeMethods.AppBarNotifications.PosChanged:
                        if (Orientation == Orientation.Vertical)
                        {
                            _appBarManager.ABSetPos(this, DesiredWidth * DpiScale, ActualHeight * DpiScale, AppBarEdge);
                        }
                        else
                        {
                            _appBarManager.ABSetPos(this, ActualWidth * DpiScale, DesiredHeight * DpiScale, AppBarEdge);
                        }
                        break;

                    case NativeMethods.AppBarNotifications.WindowArrange:
                        if ((int)lParam != 0) // before
                        {
                            Visibility = Visibility.Collapsed;
                        }
                        else // after
                        {
                            Visibility = Visibility.Visible;
                        }

                        break;
                }
                handled = true;
            }
            else if (msg == (int)NativeMethods.WM.ACTIVATE && EnableAppBar && !EnvironmentHelper.IsAppRunningAsShell && !AllowClose)
            {
                _appBarManager.AppBarActivate(hwnd);
            }
            else if (msg == (int)NativeMethods.WM.WINDOWPOSCHANGING)
            {
                // Extract the WINDOWPOS structure corresponding to this message
                NativeMethods.WINDOWPOS wndPos = NativeMethods.WINDOWPOS.FromMessage(lParam);

                // Determine if the z-order is changing (absence of SWP_NOZORDER flag)
                // If we are intentionally trying to become topmost, make it so
                if (IsRaising && (wndPos.flags & NativeMethods.SetWindowPosFlags.SWP_NOZORDER) == 0)
                {
                    // Sometimes Windows thinks we shouldn't go topmost, so poke here to make it happen.
                    wndPos.hwndInsertAfter = (IntPtr)NativeMethods.WindowZOrder.HWND_TOPMOST;
                    wndPos.UpdateMessage(lParam);
                }
            }
            else if (msg == (int)NativeMethods.WM.WINDOWPOSCHANGED && EnableAppBar && !EnvironmentHelper.IsAppRunningAsShell && !AllowClose)
            {
                _appBarManager.AppBarWindowPosChanged(hwnd);
            }
            else if (msg == (int)NativeMethods.WM.DPICHANGED)
            {
                DpiScale = (wParam.ToInt32() & 0xFFFF) / 96d;

                if (Screen.Primary)
                {
                    DpiHelper.DpiScale = DpiScale;
                }

                // suppress this if we are opening, because we're getting this message as a result of positioning
                if (!IsOpening)
                {
                    ProcessScreenChange(ScreenSetupReason.DpiChange);
                }
            }
            else if (msg == (int)NativeMethods.WM.DISPLAYCHANGE)
            {
                ProcessScreenChange(ScreenSetupReason.DisplayChange);
                handled = true;
            }
            else if (msg == (int)NativeMethods.WM.DEVICECHANGE && (int)wParam == 0x0007)
            {
                ProcessScreenChange(ScreenSetupReason.DeviceChange);
                handled = true;
            }
            else if (msg == (int)NativeMethods.WM.DWMCOMPOSITIONCHANGED)
            {
                ProcessScreenChange(ScreenSetupReason.DwmChange);
                handled = true;
            }
            
            return IntPtr.Zero;
        }
        #endregion

        #region Helpers
        private void DelaySetPosition()
        {
            // delay changing things when we are shell. it seems that explorer AppBars do this too.
            // if we don't, the system moves things to bad places
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.1) };
            timer.Start();
            timer.Tick += (sender1, args) =>
            {
                SetPosition();
                timer.Stop();
            };
        }

        public void SetScreenPosition()
        {
            // set our position if running as shell, otherwise let AppBar do the work
            if (EnvironmentHelper.IsAppRunningAsShell || !EnableAppBar)
            {
                DelaySetPosition();
            }
            else if (EnableAppBar)
            {
                if (Orientation == Orientation.Vertical)
                {
                    _appBarManager.ABSetPos(this, DesiredWidth * DpiScale, Screen.Bounds.Height, AppBarEdge);
                }
                else
                {
                    _appBarManager.ABSetPos(this, Screen.Bounds.Width, DesiredHeight * DpiScale, AppBarEdge);
                }
            }
        }

        internal void SetAppBarPosition(NativeMethods.Rect rect)
        {
            Top = rect.Top / DpiScale;
            Left = rect.Left / DpiScale;
            if (rect.Width >= 0) Width = rect.Width / DpiScale;
            if (rect.Height >= 0) Height = rect.Height / DpiScale;
        }

        private void ProcessScreenChange(ScreenSetupReason reason)
        {
            // process screen changes if we are on the primary display and the designated window
            // (or any display in the case of a DPI change, since only the changed display receives that message and not all windows receive it reliably)
            // suppress this if we are shutting down (which can trigger this method on multi-dpi setups due to window movements)
            if (((Screen.Primary && ProcessScreenChanges) || reason == ScreenSetupReason.DpiChange) && !AllowClose)
            {
                SetScreenProperties(reason);
            }
        }

        private void setFullScreenMode(bool entering)
        {
            if (entering)
            {
                ShellLogger.Debug($"AppBarWindow: {Name} on {Screen.DeviceName} conceding to full-screen app");

                Topmost = false;
                WindowHelper.ShowWindowBottomMost(Handle);
            }
            else
            {
                ShellLogger.Debug($"AppBarWindow: {Name} on {Screen.DeviceName} returning to normal state");

                IsRaising = true;
                Topmost = true;
                WindowHelper.ShowWindowTopMost(Handle);
                IsRaising = false;
            }
        }

        protected void SetBlur(bool enable)
        {
            if (EnableBlur != enable && Handle != IntPtr.Zero && AllowsTransparency)
            {
                EnableBlur = enable;
                WindowHelper.SetWindowBlur(Handle, enable);
            }
        }

        protected void RegisterAppBar()
        {
            if (!EnableAppBar || _appBarManager.AppBars.Contains(this))
            {
                return;
            }

            if (Orientation == Orientation.Vertical)
            {
                AppBarMessageId = _appBarManager.RegisterBar(this, DesiredWidth * DpiScale, ActualHeight * DpiScale, AppBarEdge);
            }
            else
            {
                AppBarMessageId = _appBarManager.RegisterBar(this, ActualWidth * DpiScale, DesiredHeight * DpiScale, AppBarEdge);
            }
        }

        protected void UnregisterAppBar()
        {
            if (!_appBarManager.AppBars.Contains(this))
            {
                return;
            }

            if (Orientation == Orientation.Vertical)
            {
                _appBarManager.RegisterBar(this, DesiredWidth * DpiScale, ActualHeight * DpiScale);
            }
            else
            {
                _appBarManager.RegisterBar(this, ActualWidth * DpiScale, DesiredHeight * DpiScale);
            }
        }
        #endregion

        #region Virtual methods
        public virtual void AfterAppBarPos(bool isSameCoords, NativeMethods.Rect rect)
        {
            if (!isSameCoords)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.1) };
                timer.Tick += (sender1, args) =>
                {
                    // set position again, since WPF may have overridden the original change from AppBarHelper
                    SetAppBarPosition(rect);

                    timer.Stop();
                };
                timer.Start();
            }
        }

        protected virtual void CustomClosing() { }

        protected virtual void SetScreenProperties(ScreenSetupReason reason)
        {
            _fullScreenHelper.NotifyScreensChanged();

            Screen = AppBarScreen.FromPrimaryScreen();
            SetScreenPosition();
        }

        public virtual void SetPosition()
        {
            double edgeOffset = 0;

            if (!RequiresScreenEdge)
            {
                edgeOffset = _appBarManager.GetAppBarEdgeWindowsHeight(AppBarEdge, Screen);
            }

            if (Orientation == Orientation.Vertical)
            {
                Top = Screen.Bounds.Top / DpiScale;
                Height = Screen.Bounds.Height / DpiScale;
                Width = DesiredWidth;

                if (AppBarEdge == AppBarEdge.Left)
                {
                    Left = Screen.Bounds.Left / DpiScale + edgeOffset;
                }
                else
                {
                    Left = Screen.Bounds.Right / DpiScale - Width - edgeOffset;
                }
            }
            else
            {
                Left = Screen.Bounds.Left / DpiScale;
                Width = Screen.Bounds.Width / DpiScale;
                Height = DesiredHeight;

                if (AppBarEdge == AppBarEdge.Top)
                {
                    Top = (Screen.Bounds.Top / DpiScale) + edgeOffset;
                }
                else
                {
                    Top = Screen.Bounds.Bottom / DpiScale - Height - edgeOffset;
                }
            }
            

            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                _appBarManager.SetWorkArea(Screen);
            }
        }
        #endregion

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}