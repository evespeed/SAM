﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Win32Interop.WinHandles;
using System.Windows.Controls.Primitives;
using SAM.Core;
using MahApps.Metro.Controls;
using System.Media;
using System.Windows.Input;
using ControlzEx.Theming;
using MahApps.Metro.Controls.Dialogs;

namespace SAM.Views
{
    /// <summary>
    /// Interaktionslogik für AccountsWindow.xaml
    /// </summary>
    public partial class AccountsWindow : MetroWindow
    {
        #region Globals

        public static List<Account> accounts;
        private static Dictionary<int, Account> actionAccounts;

        private static SAMSettings settings;

        private static List<Thread> loginThreads;
        private static List<System.Timers.Timer> timeoutTimers;

        private static readonly string updateCheckUrl = "https://raw.githubusercontent.com/rex706/SAM/master/latest.txt";
        private static readonly string repositoryUrl = "https://github.com/rex706/SAM";
        private static readonly string releasesUrl = repositoryUrl + "/releases";

        private static bool isLoadingSettings = true;
        private static bool firstLoad = true;
        private static bool steamUpdateDetected = false;

        private static readonly string dataFile = "info.dat";
        private static readonly string backupFile = dataFile + ".bak";
        private static string loadSource;

        // Keys are changed before releases/updates
        private static readonly string eKey = "PRIVATE_KEY";
        private static string ePassword = "";

        private static double originalHeight;
        private static double originalWidth;
        private static Thickness initialAddButtonGridMargin;

        private static bool exporting = false;
        private static bool deleting = false;
        private static bool loginAllSequence = false;
        private static bool loginAllCancelled = false;
        private static bool noReactLogin = false;

        private static Button holdingButton = null;
        private static bool dragging = false;
        private static System.Timers.Timer mouseHoldTimer;

        private static System.Timers.Timer autoReloadApiTimer;

        private static readonly int maxRetry = 3;

        // Resize animation variables
        private static System.Windows.Forms.Timer _Timer = new System.Windows.Forms.Timer();
        private int _Stop = 0;
        private double _RatioHeight;
        private double _RatioWidth;
        private double _Height;
        private double _Width;

        #endregion

        public AccountsWindow()
        {
            InitializeComponent();

            // If no settings file exists, create one and initialize values.
            if (!File.Exists(SAMSettings.FILE_NAME))
            {
                GenerateSettings();
            }

            LoadSettings();

            Loaded += new RoutedEventHandler(MainWindow_Loaded);
            BackgroundBorder.PreviewMouseLeftButtonDown += (s, e) => { DragMove(); };

            _Timer.Tick += new EventHandler(Timer_Tick);
            _Timer.Interval = 10;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            MenuItem ver = new MenuItem();
            MenuItem newExistMenuItem = (MenuItem)FileMenu.Items[2];
            ver.Header = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString();
            ver.IsEnabled = false;
            newExistMenuItem.Items.Add(ver);

#if DEBUG
            MenuItem windowStateMenuItem = new MenuItem();
            windowStateMenuItem.Header = "Get Window State";
            windowStateMenuItem.Click += WindowStateMenuItem_Click;
            newExistMenuItem.Items.Add(windowStateMenuItem);
#endif

            if (settings.User.CheckForUpdates)
            {
                UpdateResponse response = await UpdateHelper.CheckForUpdate(updateCheckUrl);

                switch (response)
                {
                    case UpdateResponse.Later:
                        ver.Header = "Update Available!";
                        ver.Click += Ver_Click;
                        ver.IsEnabled = true;
                        break;

                    case UpdateResponse.Update:

                        if (eKey == "PRIVATE_KEY")
                        {
                            MessageBoxResult result = MessageBox.Show(
                                "An update for SAM is available!\n\n" +
                                "Please pull the latest changes and rebuild.\n\n" +
                                "Do you understand?", 
                                "Update Available", MessageBoxButton.YesNo);

                            if (result == MessageBoxResult.No)
                            {
                                // TODO: wiki for #176
                                Process.Start("https://github.com/rex706/SAM/issues/176");
                            }

                            Close();
                            return;
                        }

                        await UpdateHelper.StartUpdate(updateCheckUrl, releasesUrl);

                        Close();
                        return;
                }
            }

            loginThreads = new List<Thread>();

            // Save New Button inital margin.
            initialAddButtonGridMargin = AddButtonGrid.Margin;

            // Save initial window height and width;
            originalHeight = Height;
            originalWidth = Width;

            // Load window with account buttons.
            RefreshWindow(dataFile);

            // Login to auto log account if enabled and Steam is not already open.
            Process[] SteamProc = Process.GetProcessesByName("Steam");

            if (SteamProc.Length == 0)
            {
                if (settings.User.LoginRecentAccount)
                {
                    Login(settings.User.RecentAccountIndex);
                }
                else if (settings.User.LoginSelectedAccount)
                {
                    Login(settings.User.SelectedAccountIndex);
                }
            }
        }

        private void WindowStateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            WindowHandle windowHandle = WindowUtils.GetSteamLoginWindow();

            if (windowHandle.IsValid)
            {
                LoginWindowState loginWindowState = WindowUtils.GetLoginWindowState(windowHandle);
                MessageBox.Show(loginWindowState.ToString());
            }
            else
            {
                MessageBox.Show("Invalid handle");
            }
        }

        private bool VerifyAndSetPassword()
        {
            MessageBoxResult messageBoxResult = MessageBoxResult.No;

            while (messageBoxResult == MessageBoxResult.No)
            {
                var passwordDialog = new PasswordWindow();

                if (passwordDialog.ShowDialog() == true && !string.IsNullOrEmpty(passwordDialog.PasswordText))
                {
                    ePassword = passwordDialog.PasswordText;

                    return true;
                }
                else if (string.IsNullOrEmpty(passwordDialog.PasswordText))
                {
                    messageBoxResult = MessageBox.Show("No password detected, are you sure?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                }
            }

            return false;
        }

        private bool VerifyPassword()
        {
            MessageBoxResult messageBoxResult = MessageBoxResult.No;

            while (messageBoxResult == MessageBoxResult.No)
            {
                var passwordDialog = new PasswordWindow();

                if (passwordDialog.ShowDialog() == true && !string.IsNullOrEmpty(passwordDialog.PasswordText))
                {
                    try
                    {
                        accounts = AccountUtils.PasswordDeserialize(dataFile, passwordDialog.PasswordText);
                        messageBoxResult = MessageBoxResult.None;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        messageBoxResult = MessageBox.Show("Invalid Password", "Invalid", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                        if (messageBoxResult == MessageBoxResult.Cancel)
                        {
                            return false;
                        }
                        else
                        {
                            return VerifyPassword();
                        }
                    }

                    return true;
                }
                else if (string.IsNullOrEmpty(passwordDialog.PasswordText))
                {
                    messageBoxResult = MessageBox.Show("No password detected, are you sure?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                }
            }

            return false;
        }

        private void GenerateSettings()
        {
            settings = new SAMSettings();

            settings.GenerateSettings();

            MessageBoxResult messageBoxResult = MessageBox.Show("Do you want to password protect SAM?", "Protect", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (messageBoxResult == MessageBoxResult.Yes)
            {
                settings.SetPasswordProtect(VerifyAndSetPassword());
            }
            else
            {
                settings.SetPasswordProtect(false);
            }
        }

        private void LoadSettings()
        {
            settings = new SAMSettings();

            isLoadingSettings = true;

            if (settings.User.StartMinimized)
            {
                WindowState = WindowState.Minimized;
            }

            // Load and validate saved window location.
            if (settings.File.KeyExists(SAMSettings.WINDOW_LEFT, SAMSettings.SECTION_LOCATION) && settings.File.KeyExists(SAMSettings.WINDOW_TOP, SAMSettings.SECTION_LOCATION))
            {
                Left = Double.Parse(settings.File.Read(SAMSettings.WINDOW_LEFT, SAMSettings.SECTION_LOCATION));
                Top = Double.Parse(settings.File.Read(SAMSettings.WINDOW_TOP, SAMSettings.SECTION_LOCATION));
                SetWindowSettingsIntoScreenArea();
            }
            else
            {
                SetWindowToCenter();
            }

            if (settings.User.ListView)
            {
                AddButtonGrid.Visibility = Visibility.Collapsed;
                buttonGrid.Visibility = Visibility.Collapsed;

                Height = settings.User.ListViewHeight;
                Width = settings.User.ListViewWidth;

                ResizeMode = ResizeMode.CanResize;

                foreach (DataGridColumn column in AccountsDataGrid.Columns)
                {
                    column.DisplayIndex = (int)settings.User.KeyValuePairs[settings.ListViewColumns[column.Header.ToString()]];
                }

                AccountsDataGrid.ItemsSource = accounts;
                AccountsDataGrid.Visibility = Visibility.Visible;

                SetMainScrollViewerBarsVisibility(ScrollBarVisibility.Auto);
            }
            else
            {
                AddButtonGrid.Visibility = Visibility.Visible;
                buttonGrid.Visibility = Visibility.Visible;
                AccountsDataGrid.Visibility = Visibility.Collapsed;
                ResizeMode = ResizeMode.CanMinimize;
            }

            if (settings.User.AutoReloadEnabled)
            {
                int interval = settings.User.AutoReloadInterval;

                if (settings.User.LastAutoReload.HasValue)
                {
                    double minutesSince = (DateTime.Now - settings.User.LastAutoReload.Value).TotalMinutes;

                    if (minutesSince < interval)
                    {
                        interval -= Convert.ToInt32(minutesSince);
                    }
                }

                if (interval <= 0)
                {
                    interval = settings.User.AutoReloadInterval;
                }

                autoReloadApiTimer = new System.Timers.Timer();
                autoReloadApiTimer.Elapsed += AutoReloadApiTimer_Elapsed;
                autoReloadApiTimer.Interval = 60000 * interval;
                autoReloadApiTimer.Start();
            }
            else
            {
                if (autoReloadApiTimer != null)
                {
                    autoReloadApiTimer.Stop();
                    autoReloadApiTimer.Dispose();
                }
            }

            if (settings.User.HeaderlessWindow)
            {
                ShowTitleBar = false;
                MainGrid.Margin = new Thickness(0, 10, 0, 0);
            }
            else
            {
                ShowTitleBar = true;
                MainGrid.Margin = new Thickness(0);
            }

            if (settings.User.TransparentWindow)
            {
                FileMenu.Visibility = Visibility.Hidden;
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#01000000");
                BorderBrush = Brushes.Transparent;
            }
            else
            {
                FileMenu.Visibility = Visibility.Visible;
                ClearValue(BackgroundProperty);
                ClearValue(BorderBrushProperty);
            }

            // Set user theme settings.
            ThemeManager.Current.ChangeTheme(Application.Current, settings.User.Theme + "." + settings.User.Accent);

            // Apply theme settings for extended toolkit and tabItem brushes.
            if (settings.User.Theme == SAMSettings.DARK_THEME)
            {
                Application.Current.Resources["xctkForegoundBrush"] = Brushes.White;
                Application.Current.Resources["xctkColorPickerBackground"] = new BrushConverter().ConvertFromString("#303030");
                Application.Current.Resources["GrayNormalBrush"] = Brushes.White;
            }
            else
            {
                Application.Current.Resources["xctkForegoundBrush"] = Brushes.Black;
                Application.Current.Resources["xctkColorPickerBackground"] = Brushes.White;
                Application.Current.Resources["GrayNormalBrush"] = Brushes.Black;
            }

            if (settings.User.PasswordProtect && string.IsNullOrEmpty(ePassword))
            {
                VerifyAndSetPassword();
            }

            AccountUtils.CheckSteamPath();
            isLoadingSettings = false;
        }

        private void AutoReloadApiTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ReloadAccountsAsync();
            });
        }

        public void RefreshWindow(string file)
        {
            loadSource = file;

            buttonGrid.Children.Clear();

            TaskBarIconLoginContextMenu.Items.Clear();
            TaskBarIconLoginContextMenu.IsEnabled = false;

            AddButtonGrid.Height = settings.User.ButtonSize;
            AddButtonGrid.Width = settings.User.ButtonSize;

            // Check if info.dat exists
            if (File.Exists(file))
            {
                MessageBoxResult messageBoxResult = MessageBoxResult.OK;

                // Deserialize file
                if (ePassword.Length > 0)
                {
                    while (messageBoxResult == MessageBoxResult.OK)
                    {
                        try
                        {
                            accounts = AccountUtils.PasswordDeserialize(file, ePassword);
                            messageBoxResult = MessageBoxResult.None;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                            messageBoxResult = MessageBox.Show("Invalid Password", "Invalid", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                            if (messageBoxResult == MessageBoxResult.Cancel)
                            {
                                Close();
                            }
                            else
                            {
                                VerifyAndSetPassword();
                            }
                        }
                    }
                }
                else
                {
                    try
                    {
                        accounts = AccountUtils.Deserialize(file);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);

                        if (file == backupFile)
                        {
                            MessageBox.Show(e.Message, "Deserialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            Close();
                        }

                        if (File.Exists(backupFile))
                        {
                            messageBoxResult = MessageBox.Show("An error has occured attempting to deserialize your .dat file.\n\n" +
                                "Would you like to try a detected backup?", "Deserialization Error", MessageBoxButton.YesNo, MessageBoxImage.Error);

                            if (messageBoxResult == MessageBoxResult.No)
                            {
                                Close();
                            }
                            else
                            {
                                RefreshWindow(backupFile);
                            }
                        }

                        return;
                    }
                }

                PostDeserializedRefresh(true);
            }
            else
            {
                accounts = new List<Account>();
                SerializeAccounts();
            }

            AccountsDataGrid.ItemsSource = accounts;

            if (firstLoad && settings.User.AutoReloadEnabled && AccountUtils.ShouldAutoReload(settings.User.LastAutoReload, settings.User.AutoReloadInterval))
            {
                firstLoad = false;
                ReloadAccountsAsync();
            }
        }

        private async Task ReloadAccount(Account account)
        {
            dynamic userJson;
            if (account.SteamId != null && account.SteamId.Length > 0)
            {
                userJson = await AccountUtils.GetUserInfoFromWebApiBySteamId(account.SteamId);
            }
            else
            {
                userJson = await AccountUtils.GetUserInfoFromConfigAndWebApi(account.Name);
            }

            if (userJson != null)
            {
                account.ProfUrl = userJson.response.players[0].profileurl;
                account.AviUrl = userJson.response.players[0].avatarfull;
                account.SteamId = userJson.response.players[0].steamid;
            }
            else
            {
                account.AviUrl = await AccountUtils.HtmlAviScrapeAsync(account.ProfUrl);
            }

            if (account.SteamId != null && account.SteamId.Length > 0 && AccountUtils.ApiKeyExists())
            {
                dynamic userBanJson = await AccountUtils.GetPlayerBansFromWebApi(account.SteamId);

                if (userBanJson != null)
                {
                    account.CommunityBanned = Convert.ToBoolean(userBanJson.CommunityBanned);
                    account.VACBanned = Convert.ToBoolean(userBanJson.VACBanned);
                    account.NumberOfVACBans = Convert.ToInt32(userBanJson.NumberOfVACBans);
                    account.NumberOfGameBans = Convert.ToInt32(userBanJson.NumberOfGameBans);
                    account.DaysSinceLastBan = Convert.ToInt32(userBanJson.DaysSinceLastBan);
                    account.EconomyBan = userBanJson.EconomyBan;
                }
            }
        }

        public async Task ReloadAccountsAsync()
        {
            SetWindowTitle("Loading");

            List<string> steamIds = new List<string>();

            foreach (Account account in accounts)
            {
                if (account.SteamId != null && account.SteamId.Length > 0)
                {
                    steamIds.Add(account.SteamId);
                }
                else
                {
                    string steamId = AccountUtils.GetSteamIdFromConfig(account.Name);
                    if (steamId != null && steamId.Length > 0)
                    {
                        account.SteamId = steamId;
                        steamIds.Add(steamId);
                    }
                    else if (account.ProfUrl != null && account.ProfUrl.Length > 0)
                    {
                        // Try to get steamId from profile URL via web API.

                        dynamic steamIdFromProfileUrl = await AccountUtils.GetSteamIdFromProfileUrl(account.ProfUrl);

                        if (steamIdFromProfileUrl != null)
                        {
                            account.SteamId = steamIdFromProfileUrl;
                            steamIds.Add(steamIdFromProfileUrl);
                        }

                        Thread.Sleep(new Random().Next(10, 16));
                    }
                }
            }

            List<dynamic> userInfos = await AccountUtils.GetUserInfosFromWepApi(new List<string>(steamIds));

            foreach (dynamic userInfosJson in userInfos)
            {
                foreach (dynamic userInfoJson in userInfosJson.response.players)
                {
                    Account account = accounts.FirstOrDefault(a => a.SteamId == Convert.ToString(userInfoJson.steamid));

                    if (account != null)
                    {
                        account.ProfUrl = userInfoJson.profileurl;
                        account.AviUrl = userInfoJson.avatarfull;
                    }
                }
            }

            if (AccountUtils.ApiKeyExists())
            {
                List<dynamic> userBans = await AccountUtils.GetPlayerBansFromWebApi(new List<string>(steamIds));

                foreach (dynamic userBansJson in userBans)
                {
                    foreach (dynamic userBanJson in userBansJson.players)
                    {
                        Account account = accounts.FirstOrDefault(a => a.SteamId == Convert.ToString(userBanJson.SteamId));

                        if (account != null)
                        {
                            account.CommunityBanned = Convert.ToBoolean(userBanJson.CommunityBanned);
                            account.VACBanned = Convert.ToBoolean(userBanJson.VACBanned);
                            account.NumberOfVACBans = Convert.ToInt32(userBanJson.NumberOfVACBans);
                            account.NumberOfGameBans = Convert.ToInt32(userBanJson.NumberOfGameBans);
                            account.DaysSinceLastBan = Convert.ToInt32(userBanJson.DaysSinceLastBan);
                            account.EconomyBan = userBanJson.EconomyBan;
                        }
                    }
                }
            }

            settings.SetLastAutoReload(DateTime.Now);

            SerializeAccounts();

            if (loginThreads.Count == 0) {
                ResetWindowTitle();
            }
        }

        private void PostDeserializedRefresh(bool seedAcc)
        {
            SetMainScrollViewerBarsVisibility(ScrollBarVisibility.Hidden);

            // Dispose and reinitialize timers each time grid is refreshed as to not clog up more resources than necessary. 
            if (timeoutTimers != null)
            {
                foreach (System.Timers.Timer timer in timeoutTimers)
                {
                    timer.Stop();
                    timer.Dispose();
                }
            }

            timeoutTimers = new List<System.Timers.Timer>();

            int bCounter = 0;
            int xCounter = 0;
            int yCounter = 0;

                                    int buttonOffset = settings.User.ButtonSize + 10; // 增加间距，从5改为10

            if (accounts != null)
            {
                foreach (var account in accounts)
                {
                    // Initialize timeout left text.
                    if (account.Timeout != null && account.Timeout > DateTime.Now)
                    {
                        account.TimeoutTimeLeft = AccountUtils.FormatTimespanString(account.Timeout.Value - DateTime.Now);
                    }
                    else
                    {
                        account.TimeoutTimeLeft = "";
                    }

                    // Initialize last login time display
                    if (account.LastLoginTime != null)
                    {
                        account.LastLoginTimeDisplay = ((DateTime)account.LastLoginTime).ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    else
                    {
                        account.LastLoginTimeDisplay = "";
                    }

                    string tempPass = StringCipher.Decrypt(account.Password, eKey);

                    if (seedAcc)
                    {
                        string temp2fa = null;
                        string steamId = null;

                        if (account.SharedSecret != null && account.SharedSecret.Length > 0)
                        {
                            temp2fa = StringCipher.Decrypt(account.SharedSecret, eKey);
                        }
                        if (account.SteamId != null && account.SteamId.Length > 0)
                        {
                            steamId = account.SteamId;
                        }
                    }

                    if (settings.User.ListView)
                    {
                        TaskBarIconLoginContextMenu.IsEnabled = true;
                        TaskBarIconLoginContextMenu.Items.Add(GenerateTaskBarMenuItem(account));

                        if (AccountUtils.AccountHasActiveTimeout(account))
                        {
                            // Set up timer event to update timeout label
                            var timeLeft = account.Timeout - DateTime.Now;

                            System.Timers.Timer timeoutTimer = new System.Timers.Timer();
                            timeoutTimers.Add(timeoutTimer);

                            timeoutTimer.Elapsed += delegate
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    TimeoutTimer_Tick(account, timeoutTimer);
                                });
                            };
                            timeoutTimer.Interval = 1000;
                            timeoutTimer.Enabled = true;
                        }
                    }
                    else
                    {
                        Grid accountButtonGrid = new Grid();

                        Button accountButton = new Button();
                        TextBlock accountText = new TextBlock();
                        TextBlock timeoutTextBlock = new TextBlock();

                        Border accountImage = new Border();

                        accountButton.Style = (Style)Resources["SAMButtonStyle"];
                        accountButton.Tag = account;
                        accountButton.Margin = new Thickness(0, 2, 0, 2); // 增加按钮上下边距

                        // 设置文本内容为账号别名或账号名
                        if (account.Alias != null && account.Alias.Length > 0)
                        {
                            accountText.Text = account.Alias;
                        }
                        else
                        {
                            accountText.Text = account.Name;
                        }

                        // 将鼠标悬浮提示改为显示账号别名(Alias)
                        if (account.Alias != null && account.Alias.Length > 0)
                        {
                            accountButton.ToolTip = account.Alias;
                        }
                        else
                        {
                            accountButton.ToolTip = account.Name;
                        }

                        accountButtonGrid.HorizontalAlignment = HorizontalAlignment.Left;
                        accountButtonGrid.VerticalAlignment = VerticalAlignment.Top;
                        accountButtonGrid.Margin = new Thickness(10 + xCounter * buttonOffset, yCounter * buttonOffset, 0, 0); // 只添加左侧10像素间隙

                        accountButton.Height = settings.User.ButtonSize;
                        accountButton.Width = settings.User.ButtonSize;
                        accountButton.BorderBrush = null;
                        accountButton.HorizontalAlignment = HorizontalAlignment.Center;
                        accountButton.VerticalAlignment = VerticalAlignment.Center;
                        accountButton.Background = Brushes.Transparent;
                        accountButton.Cursor = Cursors.Hand;

                        // 设置文本宽度，确保能够正常显示
                        accountText.Width = settings.User.ButtonSize;
                        if (settings.User.ButtonFontSize > 0)
                        {
                            accountText.FontSize = settings.User.ButtonFontSize;
                        }
                        else
                        {
                            accountText.FontSize = settings.User.ButtonSize / 8;
                        }

                        accountText.HorizontalAlignment = HorizontalAlignment.Center;
                        accountText.VerticalAlignment = VerticalAlignment.Center;
                        accountText.Margin = new Thickness(0, 7, 0, 0);
                        accountText.Padding = new Thickness(2, 2, 2, 2); // 增加内边距，使文本显示更美观
                        accountText.TextAlignment = TextAlignment.Center;
                        accountText.Foreground = Brushes.Black;
                        accountText.Background = Brushes.Transparent;
                        accountText.TextWrapping = TextWrapping.Wrap; // 添加文本换行
                        accountText.Visibility = Visibility.Visible; // 确保文本可见

                        timeoutTextBlock.Width = settings.User.ButtonSize;
                        timeoutTextBlock.FontSize = settings.User.ButtonSize / 8;
                        timeoutTextBlock.HorizontalAlignment = HorizontalAlignment.Center;
                        timeoutTextBlock.VerticalAlignment = VerticalAlignment.Center;
                        timeoutTextBlock.Padding = new Thickness(0, 0, 0, 1);
                        timeoutTextBlock.TextAlignment = TextAlignment.Center;
                        timeoutTextBlock.Foreground = new SolidColorBrush(Colors.White);
                        timeoutTextBlock.Background = new SolidColorBrush(new Color { A = 128, R = 255, G = 0, B = 0 });

                        // 设置离线模式文本块属性
                        var offlineModeTextBlock = new TextBlock();
                        offlineModeTextBlock.Width = settings.User.ButtonSize;
                        offlineModeTextBlock.FontSize = settings.User.ButtonSize / 10;
                        offlineModeTextBlock.HorizontalAlignment = HorizontalAlignment.Center;
                        offlineModeTextBlock.VerticalAlignment = VerticalAlignment.Top;
                        offlineModeTextBlock.Margin = new Thickness(0, 0, 0, 0);
                        offlineModeTextBlock.Padding = new Thickness(2, 0, 2, 0);
                        offlineModeTextBlock.TextAlignment = TextAlignment.Center;
                        offlineModeTextBlock.Foreground = new SolidColorBrush(Colors.White);
                        offlineModeTextBlock.Background = new SolidColorBrush(new Color { A = 180, R = 0, G = 120, B = 215 });
                        offlineModeTextBlock.Text = "离线登录";
                        
                        // 根据账号的离线模式设置决定是否显示离线登录标签
                        offlineModeTextBlock.Visibility = account.OfflineMode ? Visibility.Visible : Visibility.Collapsed;

                        // 添加最后登录时间文本块
                        var lastLoginTextBlock = new TextBlock();
                        lastLoginTextBlock.Width = settings.User.ButtonSize;
                        lastLoginTextBlock.FontSize = settings.User.ButtonSize / 10;
                        lastLoginTextBlock.HorizontalAlignment = HorizontalAlignment.Center;
                        lastLoginTextBlock.VerticalAlignment = VerticalAlignment.Bottom;
                        lastLoginTextBlock.Margin = new Thickness(0, 0, 0, 0);
                        lastLoginTextBlock.Padding = new Thickness(2, 0, 2, 0);
                        lastLoginTextBlock.TextAlignment = TextAlignment.Center;
                        lastLoginTextBlock.Foreground = new SolidColorBrush(Colors.White);
                        lastLoginTextBlock.Background = new SolidColorBrush(new Color { A = 180, R = 0, G = 180, B = 0 });
                        
                        // 设置最后登录时间文本
                        if (account.LastLoginTime != null)
                        {
                            lastLoginTextBlock.Text = ((DateTime)account.LastLoginTime).ToString("MM-dd HH:mm");
                            lastLoginTextBlock.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            lastLoginTextBlock.Visibility = Visibility.Collapsed;
                        }

                        accountImage.Height = settings.User.ButtonSize;
                        accountImage.Width = settings.User.ButtonSize;
                        accountImage.HorizontalAlignment = HorizontalAlignment.Center;
                        accountImage.VerticalAlignment = VerticalAlignment.Center;
                        accountImage.CornerRadius = new CornerRadius(3);

                        if (account.ProfUrl == "" || account.AviUrl == null || account.AviUrl == "" || account.AviUrl == " ")
                        {
                            accountImage.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.User.ButtonColor));
                            accountButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.User.ButtonFontColor));
                            timeoutTextBlock.Margin = new Thickness(0, 0, 0, 50);
                        }
                        else
                        {
                            try
                            {
                                ImageBrush imageBrush = new ImageBrush();
                                BitmapImage image1 = new BitmapImage(new Uri(account.AviUrl));
                                imageBrush.ImageSource = image1;
                                accountImage.Background = imageBrush;
                            }
                            catch (Exception m)
                            {
                                // Probably no internet connection or avatar url is bad.
                                Console.WriteLine("Error: " + m.Message);

                                accountImage.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.User.ButtonColor));
                                accountButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.User.ButtonFontColor));
                                timeoutTextBlock.Margin = new Thickness(0, 0, 0, 50);
                            }
                        }

                        accountButton.Click += new RoutedEventHandler(AccountButton_Click);
                        accountButton.PreviewMouseLeftButtonDown += new MouseButtonEventHandler(AccountButton_MouseDown);
                        accountButton.PreviewMouseLeftButtonUp += new MouseButtonEventHandler(AccountButton_MouseUp);
                        //accountButton.PreviewMouseMove += new MouseEventHandler(AccountButton_MouseMove);
                        accountButton.MouseLeave += new MouseEventHandler(AccountButton_MouseLeave);
                        accountButton.MouseEnter += delegate { AccountButton_MouseEnter(accountButton, accountText); };
                        accountButton.MouseLeave += delegate { AccountButton_MouseLeave(accountButton, accountText); };

                        accountButtonGrid.Children.Add(accountImage);

                        if (AccountUtils.AccountHasActiveTimeout(account))
                        {
                            // Set up timer event to update timeout label
                            var timeLeft = account.Timeout - DateTime.Now;

                            System.Timers.Timer timeoutTimer = new System.Timers.Timer();
                            timeoutTimers.Add(timeoutTimer);

                            timeoutTimer.Elapsed += delegate
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    TimeoutTimer_Tick(account, timeoutTextBlock, timeoutTimer);
                                });
                            };
                            timeoutTimer.Interval = 1000;
                            timeoutTimer.Enabled = true;
                            timeoutTextBlock.Text = AccountUtils.FormatTimespanString(timeLeft.Value);
                            timeoutTextBlock.Visibility = Visibility.Visible;

                            accountButtonGrid.Children.Add(timeoutTextBlock);
                        }

                        accountButtonGrid.Children.Add(accountText);
                        accountButtonGrid.Children.Add(accountButton);
                        accountButtonGrid.Children.Add(offlineModeTextBlock);
                        accountButtonGrid.Children.Add(lastLoginTextBlock);

                        if (!settings.User.HideBanIcons && (account.NumberOfVACBans > 0 || account.NumberOfGameBans > 0))
                        {
                            Image banInfoImage = new Image();

                            banInfoImage.HorizontalAlignment = HorizontalAlignment.Left;
                            banInfoImage.VerticalAlignment = VerticalAlignment.Top;
                            banInfoImage.Height = 14;
                            banInfoImage.Width = 14;
                            banInfoImage.Margin = new Thickness(10, 10, 10, 10);
                            banInfoImage.Source = new BitmapImage(new Uri(@"\Resources\error.png", UriKind.RelativeOrAbsolute));

                            banInfoImage.ToolTip = "VAC Bans: " + account.NumberOfVACBans +
                                "\nGame Bans: " + account.NumberOfGameBans +
                                "\nCommunity Banned: " + account.CommunityBanned +
                                "\nEconomy Ban: " + account.EconomyBan +
                                "\nDays Since Last Ban: " + account.DaysSinceLastBan;

                            accountButtonGrid.Children.Add(banInfoImage);
                        }

                        accountButton.ContextMenu = GenerateAccountContextMenu(account);
                        accountButton.ContextMenuOpening += new ContextMenuEventHandler(ContextMenu_ContextMenuOpening);

                        buttonGrid.Children.Add(accountButtonGrid);

                        TaskBarIconLoginContextMenu.IsEnabled = true;
                        TaskBarIconLoginContextMenu.Items.Add(GenerateTaskBarMenuItem(account));

                        bCounter++;
                        xCounter++;

                        if (bCounter % settings.User.AccountsPerRow == 0 && (!settings.User.HideAddButton || (settings.User.HideAddButton && bCounter != accounts.Count)))
                        {
                            yCounter++;
                            xCounter = 0;
                        }
                    }
                }

                if (!settings.User.ListView)
                {
                    if (bCounter > 0)
                    {
                        // Adjust window size and info positions
                        int xVal = settings.User.AccountsPerRow;

                        if (settings.User.HideAddButton)
                        {
                            AddButtonGrid.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            AddButtonGrid.Visibility = Visibility.Visible;
                        }

                        if (yCounter == 0 && !settings.User.HideAddButton)
                        {
                            xVal = xCounter + 1;
                        }
                        else if (yCounter == 0)
                        {
                            xVal = xCounter;
                        }

                        int newHeight = (buttonOffset * (yCounter + 1)) + 57;
                        int newWidth = (buttonOffset * xVal) + 17; // 增加右边距为10像素，与左边距保持一致

                        Resize(newHeight, newWidth);

                        // Adjust new account and export/delete buttons
                        AddButtonGrid.HorizontalAlignment = HorizontalAlignment.Left;
                        AddButtonGrid.VerticalAlignment = VerticalAlignment.Top;
                        AddButtonGrid.Margin = new Thickness((xCounter * buttonOffset) + 5, (yCounter * buttonOffset) + 25, 0, 0);
                    }
                    else
                    {
                        // Reset New Button position.
                        Resize(180, 148); // 增加窗口宽度，确保左右边距一致

                        AddButtonGrid.HorizontalAlignment = HorizontalAlignment.Center;
                        AddButtonGrid.VerticalAlignment = VerticalAlignment.Center;
                        AddButtonGrid.Margin = initialAddButtonGridMargin;
                        ResizeMode = ResizeMode.CanMinimize;
                    }
                }
            }
        }

        private MenuItem GenerateTaskBarMenuItem(Account account)
        {
            var taskBarIconLoginItem = new MenuItem();
            taskBarIconLoginItem.Tag = account;
            taskBarIconLoginItem.Click += new RoutedEventHandler(TaskbarIconLoginItem_Click);

            if (account.Alias != null && account.Alias.Length > 0)
            {
                taskBarIconLoginItem.Header = account.Alias;
            }
            else
            {
                taskBarIconLoginItem.Header = account.Name;
            }

            return taskBarIconLoginItem;
        }

        private ContextMenu GenerateAccountContextMenu(Account account)
        {
            ContextMenu accountContext = new ContextMenu();
            accountContext.FontSize = (double)Application.Current.Resources["MenuFontSize"];

            var deleteItem = new MenuItem();
            var editItem = new MenuItem();
            var exportItem = new MenuItem();
            var reloadItem = new MenuItem();
            var offlineLoginItem = new MenuItem(); // 离线登录菜单项

            var setTimeoutItem = new MenuItem();
            var thirtyMinuteTimeoutItem = new MenuItem();
            var twoHourTimeoutItem = new MenuItem();
            var twentyOneHourTimeoutItem = new MenuItem();
            var twentyFourHourTimeoutItem = new MenuItem();
            var sevenDayTimeoutItem = new MenuItem();
            var customTimeoutItem = new MenuItem();
            var clearTimeoutItem = new MenuItem();

            var copyMenuItem = new MenuItem();
            var copyUsernameItem = new MenuItem();
            var copyPasswordItem = new MenuItem();
            var copyProfileUrlItem = new MenuItem();
            var copySteamIdItem = new MenuItem();
            var copyMFATokenItem = new MenuItem();

            thirtyMinuteTimeoutItem.Header = "30 Minutes";
            twoHourTimeoutItem.Header = "2 Hours";
            twentyOneHourTimeoutItem.Header = "21 Hours";
            twentyFourHourTimeoutItem.Header = "24 Hours";
            sevenDayTimeoutItem.Header = "7 Days";
            customTimeoutItem.Header = "Custom";
            clearTimeoutItem.Header = "Clear";

            setTimeoutItem.Items.Add(thirtyMinuteTimeoutItem);
            setTimeoutItem.Items.Add(twoHourTimeoutItem);
            setTimeoutItem.Items.Add(twentyOneHourTimeoutItem);
            setTimeoutItem.Items.Add(twentyFourHourTimeoutItem);
            setTimeoutItem.Items.Add(sevenDayTimeoutItem);
            setTimeoutItem.Items.Add(customTimeoutItem);
            setTimeoutItem.Items.Add(clearTimeoutItem);

            if (!AccountUtils.AccountHasActiveTimeout(account))
            {
                clearTimeoutItem.IsEnabled = false;
            }

            deleteItem.Header = "Delete";
            deleteItem.Foreground = Brushes.Red;

            editItem.Header = "Edit";
            exportItem.Header = "Export";
            reloadItem.Header = "Reload";
            offlineLoginItem.Header = "离线模式";
            offlineLoginItem.IsCheckable = true;
            offlineLoginItem.IsChecked = account.OfflineMode;
            setTimeoutItem.Header = "Timeout";
            copyMenuItem.Header = "Copy";
            copyUsernameItem.Header = "Username";
            copyPasswordItem.Header = "Password";
            copyProfileUrlItem.Header = "Profile";
            copySteamIdItem.Header = "SteamID";
            copyMFATokenItem.Header = "Guard Token";

            deleteItem.Click += delegate { DeleteEntry(account); };
            editItem.Click += delegate { EditEntryAsync(account); };
            exportItem.Click += delegate { ExportAccount(account); };
            reloadItem.Click += async delegate { await ReloadAccount_ClickAsync(account); };
            offlineLoginItem.Click += delegate { 
                account.OfflineMode = !account.OfflineMode; 
                SerializeAccounts();
            };
            thirtyMinuteTimeoutItem.Click += delegate { AccountButtonSetTimeout_Click(account, DateTime.Now.AddMinutes(30)); };
            twoHourTimeoutItem.Click += delegate { AccountButtonSetTimeout_Click(account, DateTime.Now.AddHours(2)); };
            twentyOneHourTimeoutItem.Click += delegate { AccountButtonSetTimeout_Click(account, DateTime.Now.AddHours(21)); };
            twentyFourHourTimeoutItem.Click += delegate { AccountButtonSetTimeout_Click(account, DateTime.Now.AddDays(1)); };
            sevenDayTimeoutItem.Click += delegate { AccountButtonSetTimeout_Click(account, DateTime.Now.AddDays(7)); };
            customTimeoutItem.Click += delegate { AccountButtonSetCustomTimeout_Click(account); };
            clearTimeoutItem.Click += delegate { AccountButtonClearTimeout_Click(account); };
            copyUsernameItem.Click += delegate { CopyUsernameToClipboard(account); };
            copyPasswordItem.Click += delegate { CopyPasswordToClipboard(account); };
            copyProfileUrlItem.Click += delegate { CopyProfileUrlToClipboard(account); };
            copySteamIdItem.Click += delegate { CopySteamIdToClipboard(account); };

            accountContext.Items.Add(editItem);
            accountContext.Items.Add(exportItem);
            accountContext.Items.Add(reloadItem);
            accountContext.Items.Add(offlineLoginItem);
            accountContext.Items.Add(copyMenuItem);
            accountContext.Items.Add(setTimeoutItem);

            copyMenuItem.Items.Add(copyUsernameItem);
            copyMenuItem.Items.Add(copyPasswordItem);
            copyMenuItem.Items.Add(copyProfileUrlItem);
            copyMenuItem.Items.Add(copySteamIdItem);

            string sharedSecret = StringCipher.Decrypt(account.SharedSecret, eKey);

            if (!string.IsNullOrEmpty(sharedSecret))
            {
                copyMFATokenItem.Click += delegate { Copy2FA(account); };
            }
            else
            {
                copyMFATokenItem.IsEnabled = false;
            }

            copyMenuItem.Items.Add(copyMFATokenItem);
            accountContext.Items.Add(deleteItem);

            return accountContext;
        }

        private ContextMenu GenerateAltActionContextMenu(string altActionType)
        {
            ContextMenu contextMenu = new ContextMenu();
            var actionMenuItem = new MenuItem();

            if (altActionType == AltActionType.DELETING)
            {
                actionMenuItem.Header = "Delete Selected";
                actionMenuItem.Click += delegate { DeleteSelectedAccounts(); };
            }
            else if (altActionType == AltActionType.EXPORTING)
            {
                actionMenuItem.Header = "Export Selected";
                actionMenuItem.Click += delegate { ExportSelectedAccounts(); };
            }

            var cancelMenuItem = new MenuItem();
            cancelMenuItem.Header = "Cancel";
            cancelMenuItem.Click += delegate { ResetFromExportOrDelete(); };

            contextMenu.Items.Add(actionMenuItem);
            contextMenu.Items.Add(cancelMenuItem);

            return contextMenu;
        }

        private async void AddAccount()
        {
            // User entered info
            var dialog = new AccountInfoDialog();

            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.AccountText) && !string.IsNullOrEmpty(dialog.PasswordText))
            {
                string aviUrl;
                if (!string.IsNullOrEmpty(dialog.AviText))
                {
                    aviUrl = dialog.AviText;
                }
                else
                {
                    aviUrl = await AccountUtils.HtmlAviScrapeAsync(dialog.UrlText);
                }

                string steamId = dialog.SteamId;

                try
                {
                    Account newAccount = new Account() {
                        Name = dialog.AccountText,
                        Alias = dialog.AliasText,
                        Password = StringCipher.Encrypt(dialog.PasswordText, eKey),
                        SharedSecret = StringCipher.Encrypt(dialog.SharedSecretText, eKey),
                        ProfUrl = dialog.UrlText,
                        AviUrl = aviUrl,
                        SteamId = steamId,
                        Parameters = dialog.ParametersText,
                        Description = dialog.DescriptionText,
                        FriendsLoginStatus = dialog.FriendsLoginStatus
                    };

                    await ReloadAccount(newAccount);

                    accounts.Add(newAccount);

                    if (dialog.AutoLogAccountIndex)
                    {
                        settings.EnableSelectedAccountIndex(accounts.Count);
                    }

                    SerializeAccounts();
                }
                catch (Exception m)
                {
                    MessageBox.Show(m.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                    SerializeAccounts();
                    AddAccount();
                }
            }
        }

        private async Task EditEntryAsync(Account account)
        {
            var dialog = new AccountInfoDialog
            {
                AccountText = account.Name,
                AliasText = account.Alias,
                PasswordText = StringCipher.Decrypt(account.Password, eKey),
                SharedSecretText = StringCipher.Decrypt(account.SharedSecret, eKey),
                UrlText = account.ProfUrl,
                SteamId = account.SteamId,
                ParametersText = account.Parameters,
                DescriptionText = account.Description,
                FriendsLoginStatus = account.FriendsLoginStatus
            };

            int index = accounts.FindIndex(a => a.GetHashCode() == account.GetHashCode());

            if (settings.IsLoginSelectedEnabled() && settings.User.SelectedAccountIndex == index)
            {
                dialog.autoLogCheckBox.IsChecked = true;
            }
                
            if (dialog.ShowDialog() == true)
            {
                string aviUrl;
                if (dialog.AviText != null && dialog.AviText.Length > 1)
                {
                    aviUrl = dialog.AviText;
                }
                else
                {
                    aviUrl = await AccountUtils.HtmlAviScrapeAsync(dialog.UrlText);
                }

                // If the auto login checkbox was checked, update settings file and global variables. 
                if (dialog.AutoLogAccountIndex)
                {
                    settings.EnableSelectedAccountIndex(index);
                }
                else if (index == settings.User.SelectedAccountIndex)
                {
                    settings.ResetSelectedAccountIndex();
                }

                try
                {
                    account.Name = dialog.AccountText;
                    account.Alias = dialog.AliasText;
                    account.Password = StringCipher.Encrypt(dialog.PasswordText, eKey);
                    account.SharedSecret = StringCipher.Encrypt(dialog.SharedSecretText, eKey);
                    account.ProfUrl = dialog.UrlText;
                    account.AviUrl = aviUrl;
                    account.SteamId = dialog.SteamId;
                    account.Parameters = dialog.ParametersText;
                    account.Description = dialog.DescriptionText;
                    account.FriendsLoginStatus = dialog.FriendsLoginStatus;

                    SerializeAccounts();
                }
                catch (Exception m)
                {
                    MessageBox.Show(m.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    EditEntryAsync(account);
                }
            }
        }

        private void DeleteEntry(Account account)
        {
            MessageBoxResult result = MessageBox.Show("Are you sure you want to delete this entry?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);

            if (result == MessageBoxResult.Yes)
            {
                accounts.Remove(account);
                SerializeAccounts();
            }
        }

        private void Login(int index)
        {
            if (index >= 0 && index < accounts.Count)
            {
                Login(accounts[index]);
            }
        }

        private void Login(Account account)
        {
            if (!settings.User.SandboxMode)
            {
                foreach (Thread loginThread in loginThreads)
                {
                    loginThread.Abort();
                }
            }

            MainGrid.IsEnabled = settings.User.SandboxMode;
            SetWindowTitle("Working");

            new Thread(() => {
                try
                {
                    // 根据账户的OfflineMode属性决定使用正常登录还是离线登录
                    if (account.OfflineMode)
                    {
                        // 关闭已经运行的Steam
                        if (!settings.User.SandboxMode)
                        {
                            ShutdownSteam();
                        }

                        // 设置注册表中的AutoLoginUser键
                        AccountUtils.SetAutoLoginUserForOfflineMode(account);

                        // 启动Steam，无需额外参数
                        ProcessStartInfo startInfo = new ProcessStartInfo
                        {
                            FileName = settings.User.SteamPath + "steam.exe",
                            WorkingDirectory = settings.User.SteamPath,
                            UseShellExecute = true,
                            Arguments = ""
                        };

                        try
                        {
                            Process.Start(startInfo);
                            
                            // 更新最后登录时间
                            account.LastLoginTime = DateTime.Now;
                            account.LastLoginTimeDisplay = ((DateTime)account.LastLoginTime).ToString("yyyy-MM-dd HH:mm:ss");
                            
                            // 使用Dispatcher.Invoke确保在UI线程上调用SerializeAccounts
                            Dispatcher.Invoke(() => {
                                SerializeAccounts();
                            });
                        }
                        catch (Exception m)
                        {
                            MessageBox.Show("启动Steam时出错\n\n" + m.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    else
                    {
                        // 使用正常登录方式
                        Login(account, 0);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
                finally
                {
                    ResetWindowTitle();
                    steamUpdateDetected = false;
                    int index = accounts.FindIndex(a => a.GetHashCode() == account.GetHashCode());
                    settings.UpdateRecentAccountIndex(index);
                }
            }).Start();
        }

        private void Login(Account account, int tryCount)
        {
            if (tryCount == 0)
            {
                Thread currentThread = Thread.CurrentThread;
                currentThread.Name = accounts.IndexOf(account).ToString();
                loginThreads.Add(currentThread);
            }

            if (tryCount == maxRetry)
            {
                MessageBox.Show("Login Failed! Please try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (AccountUtils.AccountHasActiveTimeout(account))
            {
                MessageBoxResult result = MessageBox.Show("Account timeout is active!\nLogin anyway?", "Timeout", MessageBoxButton.YesNo, MessageBoxImage.Warning, 0, MessageBoxOptions.DefaultDesktopOnly);

                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }

            // Verify Steam file path.
            settings.User.SteamPath = AccountUtils.CheckSteamPath();

            if (!settings.User.SandboxMode)
            {
                ShutdownSteam();
            }

            // Make sure Username field is empty and Remember Password checkbox is unchecked.
            AccountUtils.ClearAutoLoginUserKeyValues();

            StringBuilder parametersBuilder = new StringBuilder();
            List<string> parameters = settings.globalParameters;

            if (account.FriendsLoginStatus != FriendsLoginStatus.Unchanged && account.SteamId != null && account.SteamId.Length > 0)
            {
                AccountUtils.SetFriendsOnlineMode(account.FriendsLoginStatus, account.SteamId, settings.User.SteamPath);
            }

            if (account.HasParameters)
            {
                parameters = account.Parameters.Split(' ').ToList();
                noReactLogin = account.Parameters.Contains("-noreactlogin");
            }
            else if (settings.User.CustomParameters)
            {
                parametersBuilder.Append(settings.User.CustomParametersValue).Append(" ");
                noReactLogin = settings.User.CustomParametersValue.Contains("-noreactlogin");
            }

            foreach (string parameter in parameters)
            {
                parametersBuilder.Append(parameter).Append(" ");
            }

            string startParams = parametersBuilder.ToString();

            // Start Steam process with the selected path.
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = settings.User.SteamPath + "steam.exe",
                WorkingDirectory = settings.User.SteamPath,
                UseShellExecute = true,
                Arguments = startParams
            };

            Process steamProcess;

            try
            {
                steamProcess = Process.Start(startInfo);
            }
            catch (Exception m)
            {
                MessageBox.Show("There was an error starting Steam\n\n" + m.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // -noreactlogin parameter has been depecrated as of January 2023
            if (noReactLogin)
            {
                TypeCredentials(steamProcess, account, tryCount);
            }
            else
            {
                EnterCredentials(steamProcess, account, 0);
            }
        }

        private void TypeCredentials(Process steamProcess, Account account, int tryCount)
        {
            WindowHandle steamLoginWindow = WindowUtils.GetLegacySteamLoginWindow();

            while (!steamLoginWindow.IsValid)
            {
                Thread.Sleep(100);
                steamLoginWindow = WindowUtils.GetLegacySteamLoginWindow();
            }

            Process steamLoginProcess = WindowUtils.WaitForSteamProcess(steamLoginWindow);
            steamLoginProcess.WaitForInputIdle();

            Thread.Sleep(settings.User.SleepTime);
            WindowUtils.SetForegroundWindow(steamLoginWindow.RawPtr);
            Thread.Sleep(100);

            // Enable Caps-Lock, to prevent IME problems.
            bool capsLockEnabled = System.Windows.Forms.Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock);
            if (settings.User.HandleMicrosoftIME && !settings.User.IME2FAOnly && !capsLockEnabled)
            {
                WindowUtils.SendCapsLockGlobally();
            }

            foreach (char c in account.Name.ToCharArray())
            {
                WindowUtils.SetForegroundWindow(steamLoginWindow.RawPtr);
                Thread.Sleep(10);
                WindowUtils.SendCharacter(steamLoginWindow.RawPtr, settings.User.VirtualInputMethod, c);
            }

            Thread.Sleep(100);
            WindowUtils.SendTab(steamLoginWindow.RawPtr, settings.User.VirtualInputMethod);
            Thread.Sleep(100);

            foreach (char c in account.Password.ToCharArray())
            {
                WindowUtils.SetForegroundWindow(steamLoginWindow.RawPtr);
                Thread.Sleep(10);
                WindowUtils.SendCharacter(steamLoginWindow.RawPtr, settings.User.VirtualInputMethod, c);
            }

            if (settings.User.RememberPassword)
            {
                WindowUtils.SetForegroundWindow(steamLoginWindow.RawPtr);

                Thread.Sleep(100);
                WindowUtils.SendTab(steamLoginWindow.RawPtr, settings.User.VirtualInputMethod);
                Thread.Sleep(100);
                WindowUtils.SendSpace(steamLoginWindow.RawPtr, settings.User.VirtualInputMethod);
            }

            WindowUtils.SetForegroundWindow(steamLoginWindow.RawPtr);

            Thread.Sleep(100);
            WindowUtils.SendEnter(steamLoginWindow.RawPtr, settings.User.VirtualInputMethod);

            // Restore CapsLock back if CapsLock is off before we start typing.
            if (settings.User.HandleMicrosoftIME && !settings.User.IME2FAOnly && !capsLockEnabled)
            {
                WindowUtils.SendCapsLockGlobally();
            }

            int waitCount = 0;

            // Only handle 2FA if shared secret was entered.
            if (account.SharedSecret != null && account.SharedSecret.Length > 0)
            {
                WindowHandle steamGuardWindow = WindowUtils.GetLegacySteamGuardWindow();

                while (!steamGuardWindow.IsValid && waitCount < maxRetry)
                {
                    Thread.Sleep(settings.User.SleepTime);

                    steamGuardWindow = WindowUtils.GetLegacySteamGuardWindow();

                    // Check for Steam warning window.
                    WindowHandle steamWarningWindow = WindowUtils.GetLegacySteamWarningWindow();
                    if (steamWarningWindow.IsValid)
                    {
                        //Cancel the 2FA process since Steam connection is likely unavailable. 
                        return;
                    }

                    waitCount++;
                }

                // 2FA window not found, login probably failed. Try again.
                if (waitCount == maxRetry)
                {
                    Dispatcher.Invoke(delegate () { Login(account, tryCount + 1); });
                    return;
                }

                Handle2FA(steamProcess, account);
            }
            else
            {
                PostLogin();
            }
        }

        private void EnterCredentials(Process steamProcess, Account account, int tryCount)
        {
            if (steamProcess.HasExited)
            {
                return;
            }

            if (tryCount > 0 && WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
            {
                PostLogin();
                return;
            }

            WindowHandle steamLoginWindow = WindowUtils.GetSteamLoginWindow(steamProcess);

            while (!steamLoginWindow.IsValid)
            {
                if (steamProcess.HasExited)
                {
                    if (steamUpdateDetected && steamProcess.ExitCode == SteamExitCode.SUCCESS)
                    {
                        // Update window creates a new steam process
                        Process process = WindowUtils.GetSteamProcess();
                        if (process != null)
                        {
                            steamProcess = process;
                        }
                    }
                    else
                    {
                        return;
                    }
                }

                if (WindowUtils.IsSteamUpdating(steamProcess))
                {
                    steamUpdateDetected = true;
                    SetWindowTitle("Waiting");
                }

                Thread.Sleep(100);
                steamLoginWindow = WindowUtils.GetSteamLoginWindow(steamProcess);
            }

            SetWindowTitle("Working");
            LoginWindowState state = LoginWindowState.None;

            while (state != LoginWindowState.Success && state != LoginWindowState.Code && state != LoginWindowState.Invalid)
            {
                if (steamProcess.HasExited || state == LoginWindowState.Error)
                {
                    return;
                }

                Thread.Sleep(100);

                state = WindowUtils.GetLoginWindowState(steamLoginWindow);

                if (state == LoginWindowState.Selection)
                {
                    WindowUtils.HandleAccountSelection(steamLoginWindow);
                    continue;
                }

                if (state == LoginWindowState.Login)
                {
                    string password = StringCipher.Decrypt(account.Password, eKey);
                    state = WindowUtils.TryCredentialsEntry(steamLoginWindow, account.Name, password, settings.User.RememberPassword);
                }
            }

            // 如果是Invalid状态，可能是Steam已经成功登录并且登录窗口已关闭
            // 检查Steam主窗口是否已打开
            if (state == LoginWindowState.Invalid && WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
            {
                PostLogin();
                return;
            }

            if (account.SharedSecret != null && account.SharedSecret.Length > 0)
            {
                EnterReact2FA(steamProcess, account, tryCount);
            }
            else
            {
                Thread.Sleep(settings.User.SleepTime);
                state = LoginWindowState.Loading;

                while (state == LoginWindowState.Loading)
                {
                    Thread.Sleep(100);
                    state = WindowUtils.GetLoginWindowState(steamLoginWindow);
                    
                    // 检查Steam主窗口是否已打开
                    if (state == LoginWindowState.Invalid && WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
                    {
                        break;
                    }
                }

                PostLogin();
            }
        }

        private void Copy2FA(Account account)
        {
            string sharedSecret = StringCipher.Decrypt(account.SharedSecret, eKey);
            string key = WindowUtils.Generate2FACode(sharedSecret);
            Clipboard.SetText(key);
            Task.Run(() => {
                SystemSounds.Beep.Play();
                return Task.CompletedTask;
            });
        }

        private void Handle2FA(Process steamProcess, Account account)
        {
            if (noReactLogin)
            {
                Type2FA(steamProcess, account, 0);
            }
            else
            {
                EnterReact2FA(steamProcess, account, 0);
            }
        }

        private void EnterReact2FA(Process steamProcess, Account account, int tryCount)
        {
            int retry = tryCount + 1;

            if (steamProcess.HasExited)
            {
                return;
            }

            if (tryCount > 0 && WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
            {
                PostLogin();
                return;
            }

            WindowHandle steamLoginWindow = WindowUtils.GetSteamLoginWindow(steamProcess);

            int maxWaitTime = 30; // 最大等待时间，单位：秒
            int waitCounter = 0;
            
            while (!steamLoginWindow.IsValid)
            {
                Thread.Sleep(100);
                steamLoginWindow = WindowUtils.GetSteamLoginWindow(steamProcess);
                
                // 检查Steam主窗口是否已打开，如果已打开则说明已成功登录
                if (WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
                {
                    PostLogin();
                    return;
                }
                
                // 超时检查
                waitCounter++;
                if (waitCounter >= maxWaitTime * 10) // 100ms * 10 = 1秒
                {
                    MessageBox.Show("等待Steam登录窗口超时，可能已经登录成功或Steam出现异常", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            LoginWindowState state = LoginWindowState.None;
            string secret = StringCipher.Decrypt(account.SharedSecret, eKey);

            while (state != LoginWindowState.Success && state != LoginWindowState.Invalid)
            {
                if (steamProcess.HasExited || state == LoginWindowState.Error)
                {
                    return;
                }
                else if (state == LoginWindowState.Login || state == LoginWindowState.Selection)
                {
                    EnterCredentials(steamProcess, account, retry);
                    return;
                }
                else if (WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
                {
                    PostLogin();
                    return;
                }

                Thread.Sleep(100);

                state = WindowUtils.GetLoginWindowState(steamLoginWindow);

                if (state == LoginWindowState.Code)
                {
                    state = WindowUtils.TryCodeEntry(steamLoginWindow, secret);
                }
            }

            // 如果是Invalid状态，检查Steam主窗口是否已打开
            if (state == LoginWindowState.Invalid && WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
            {
                PostLogin();
                return;
            }

            Thread.Sleep(settings.User.SleepTime);
            state = LoginWindowState.Loading;

            while (state == LoginWindowState.Loading)
            {
                Thread.Sleep(100);
                state = WindowUtils.GetLoginWindowState(steamLoginWindow);
                
                // 检查Steam主窗口是否已打开
                if (state == LoginWindowState.Invalid && WindowUtils.GetMainSteamClientWindow(steamProcess).IsValid)
                {
                    break;
                }
            }

            steamLoginWindow = WindowUtils.GetSteamLoginWindow(steamProcess);

            if (tryCount < maxRetry && steamLoginWindow.IsValid)
            {
                Console.WriteLine("2FA code might have failed, attempting retry " + retry + "...");
                EnterReact2FA(steamProcess, account, retry);
                return;
            }
            else if (tryCount == maxRetry && steamLoginWindow.IsValid)
            {
                MessageBox.Show("2FA Failed\nPlease verify your shared secret is correct!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            PostLogin();
        }

        private void Type2FA(Process steamProcess, Account account, int tryCount)
        {
            if (tryCount > 0 && WindowUtils.GetLegacyMainSteamClientWindow().IsValid)
            {
                PostLogin();
                return;
            }

            // Need both the Steam Login and Steam Guard windows.
            // Can't focus the Steam Guard window directly.
            var steamLoginWindow = WindowUtils.GetLegacySteamLoginWindow();
            var steamGuardWindow = WindowUtils.GetLegacySteamGuardWindow();

            while (!steamLoginWindow.IsValid || !steamGuardWindow.IsValid)
            {
                Thread.Sleep(100);
                steamLoginWindow = WindowUtils.GetLegacySteamLoginWindow();
                steamGuardWindow = WindowUtils.GetLegacySteamGuardWindow();

                // Check for Steam warning window.
                var steamWarningWindow = WindowUtils.GetLegacySteamWarningWindow();
                if (steamWarningWindow.IsValid)
                {
                    //Cancel the 2FA process since Steam connection is likely unavailable. 
                    return;
                }
            }

            Console.WriteLine("Found windows.");

            // Generate 2FA code, then send it to the client.
            Console.WriteLine("It is idle now, typing code...");

            WindowUtils.SetForegroundWindow(steamGuardWindow.RawPtr);

            // Enable Caps-Lock, to prevent IME problems.
            bool capsLockEnabled = System.Windows.Forms.Control.IsKeyLocked(System.Windows.Forms.Keys.CapsLock);
            if (settings.User.HandleMicrosoftIME && !capsLockEnabled)
            {
                WindowUtils.SendCapsLockGlobally();
            }

            Thread.Sleep(100);

            string sharedSecret = StringCipher.Decrypt(account.SharedSecret, eKey);

            foreach (char c in WindowUtils.Generate2FACode(sharedSecret))
            {
                WindowUtils.SetForegroundWindow(steamGuardWindow.RawPtr);
                Thread.Sleep(10);

                // Can also send keys to login window handle, but nothing works unless it is the foreground window.
                WindowUtils.SendCharacter(steamGuardWindow.RawPtr, settings.User.VirtualInputMethod, c);
            }

            WindowUtils.SetForegroundWindow(steamGuardWindow.RawPtr);
            Thread.Sleep(100);
            WindowUtils.SendEnter(steamGuardWindow.RawPtr, settings.User.VirtualInputMethod);
            
            // Restore CapsLock back if CapsLock is off before we start typing.
            if (settings.User.HandleMicrosoftIME && !capsLockEnabled)
            {
                WindowUtils.SendCapsLockGlobally();
            }

            // Need a little pause here to more reliably check for popup later.
            Thread.Sleep(settings.User.SleepTime);

            // Check if we still have a 2FA popup, which means the previous one failed.
            steamGuardWindow = WindowUtils.GetLegacySteamGuardWindow();

            int retry = tryCount + 1;

            if (tryCount < maxRetry && steamGuardWindow.IsValid)
            {
                Console.WriteLine("2FA code might have failed, attempting retry " + retry + "...");
                Type2FA(steamProcess, account, retry);
                return;
            }
            else if (tryCount == maxRetry && steamGuardWindow.IsValid)
            {
                MessageBoxResult result = MessageBox.Show("2FA Failed\nPlease wait or bring the Steam Guard\nwindow to the front before clicking OK", "Error", MessageBoxButton.OKCancel, MessageBoxImage.Error);

                if (result == MessageBoxResult.OK)
                {
                    Type2FA(steamProcess, account, retry);
                }
            }
            else if (tryCount == maxRetry + 1 && steamGuardWindow.IsValid)
            {
                MessageBox.Show("2FA Failed\nPlease verify your shared secret is correct!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            PostLogin();
        }

        private void PostLogin()
        {
            // 更新当前账户的最后登录时间
            if (accounts != null && accounts.Count > 0)
            {
                // 找到当前登录的账户
                Account currentAccount = null;
                foreach (Thread thread in loginThreads)
                {
                    if (thread.IsAlive && thread.Name != null)
                    {
                        int index = Convert.ToInt32(thread.Name);
                        if (index >= 0 && index < accounts.Count)
                        {
                            currentAccount = accounts[index];
                            break;
                        }
                    }
                }

                if (currentAccount != null)
                {
                    // 更新最后登录时间
                    currentAccount.LastLoginTime = DateTime.Now;
                    currentAccount.LastLoginTimeDisplay = ((DateTime)currentAccount.LastLoginTime).ToString("yyyy-MM-dd HH:mm:ss");
                    
                    // 使用Dispatcher.Invoke确保在UI线程上调用SerializeAccounts
                    Dispatcher.Invoke(() => {
                        SerializeAccounts();
                    });
                }
            }

            if (!loginAllSequence)
            {
                if (settings.User.ClearUserData)
                {
                    WindowUtils.ClearSteamUserDataFolder(settings.User.SteamPath, settings.User.SleepTime, maxRetry);
                }
                if (settings.User.CloseOnLogin)
                {
                    Dispatcher.Invoke(delegate () { Close(); });
                }
            }
        }

        private void SortAccounts(SortType sortType)
        {
            if (accounts.Count > 0)
            {
                int? hash = null;
                int index = -1;

                if (settings.User.LoginSelectedAccount)
                {
                    hash = accounts[settings.User.SelectedAccountIndex].GetHashCode();
                }
                else if (settings.User.LoginRecentAccount)
                {
                    hash = accounts[settings.User.RecentAccountIndex].GetHashCode();
                }

                switch (sortType)
                {
                    case SortType.Username:
                        accounts = accounts.OrderBy(x => x.Name).ToList();
                        break;
                    case SortType.Alias:
                        accounts = accounts.OrderBy(x => x.Alias).ToList();
                        break;
                    case SortType.Banned:
                        accounts = accounts.OrderByDescending(x => x.DaysSinceLastBan).ToList();
                        break;
                    case SortType.Random:
                        accounts = accounts.OrderBy(x => Guid.NewGuid()).ToList();
                        break;
                    case SortType.LastLogin:
                        accounts = accounts.OrderByDescending(x => x.LastLoginTime ?? DateTime.MinValue).ToList();
                        break;
                }

                if (hash != null)
                {
                    index = accounts.FindIndex(a => a.GetHashCode() == hash);

                    if (settings.User.LoginSelectedAccount)
                    {
                        settings.EnableSelectedAccountIndex(index);
                    }
                    else if (settings.User.LoginRecentAccount)
                    {
                        settings.UpdateRecentAccountIndex(index);
                    }
                }

                // 使用Dispatcher.Invoke确保在UI线程上调用SerializeAccounts
                Dispatcher.Invoke(() => {
                    SerializeAccounts();
                });
            }
        }

        private void SerializeAccounts()
        {
            if (loadSource != backupFile && File.Exists(dataFile))
            {
                File.Delete(backupFile);
                File.Copy(dataFile, backupFile);
            }

            if (IsPasswordProtected() && !string.IsNullOrEmpty(ePassword))
            {
                AccountUtils.PasswordSerialize(accounts, ePassword);
            }
            else
            {
                AccountUtils.Serialize(accounts);
            }

            // 使用Dispatcher.Invoke确保UI操作在UI线程上执行
            Dispatcher.Invoke(() =>
            {
                RefreshWindow(dataFile);
            });
        }

        private void ExportAccount(Account account)
        {
            AccountUtils.ExportSelectedAccounts(new List<Account>() { account });
        }

        #region Resize and Resize Timer

        public void Resize(double _PassedHeight, double _PassedWidth)
        {
            _Height = _PassedHeight;
            _Width = _PassedWidth;

            _Timer.Enabled = true;
            _Timer.Start();
        }

        private void Timer_Tick(Object myObject, EventArgs myEventArgs)
        {
            if (_Stop == 0)
            {
                _RatioHeight = ((Height - _Height) / 5) * -1;
                _RatioWidth = ((Width - _Width) / 5) * -1;
            }
            _Stop++;

            Height += _RatioHeight;
            Width += _RatioWidth;

            if (_Stop == 5)
            {
                _Timer.Stop();
                _Timer.Enabled = false;
                _Timer.Dispose();

                _Stop = 0;

                Height = _Height;
                Width = _Width;

                SetMainScrollViewerBarsVisibility(ScrollBarVisibility.Auto);
            }
        }

        #endregion

        #region Click Events

        private void AccountButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Opacity = 0.5;

                holdingButton = btn;

                mouseHoldTimer = new System.Timers.Timer(1000);
                mouseHoldTimer.Elapsed += MouseHoldTimer_Elapsed;
                mouseHoldTimer.Enabled = true;
                mouseHoldTimer.Start();
            }
        }

        private void MouseHoldTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            mouseHoldTimer.Stop();

            if (holdingButton != null)
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => holdingButton.Opacity = 1));
                dragging = true;
            }
        }

        private void AccountButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button btn)
            {
                holdingButton = null;
                btn.Opacity = 1;
                dragging = false;
            }
        }

        private void AccountButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Button btn)
            {
                holdingButton = null;
                btn.Opacity = 1;
                dragging = false;
            }
        }

        private void AccountButton_MouseLeave(Button accountButton, TextBlock accountText)
        {
            // 鼠标离开时恢复原来的文字颜色
            accountText.Foreground = Brushes.Black;
        }

        private void AccountButton_MouseEnter(Button accountButton, TextBlock accountText)
        {
            // 鼠标悬浮时文字变红
            accountText.Foreground = Brushes.Red;
        }

        private void AccountButton_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is Button btn)
            {
                if (!dragging)
                {
                    return;
                }

                btn.Opacity = 1;

                Point mousePoint = e.GetPosition(this);

                int marginLeft = (int)mousePoint.X - ((int)btn.Width / 2);
                int marginTop = (int)mousePoint.Y - ((int)btn.Height / 2);

                btn.Margin = new Thickness(marginLeft, marginTop, 0, 0);
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            AddAccount();
        }

        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                Login((Account)btn.Tag);
            }
        }

        private void TaskbarIconLoginItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item)
            {
                Login((Account)item.Tag);
            }
        }

        private void AccountButtonSetTimeout_Click(Account account, DateTime timeout)
        {
            if (timeout != null && timeout != new DateTime())
            {
                account.Timeout = timeout;
                SerializeAccounts();
            }
        }

        private void AccountButtonSetCustomTimeout_Click(Account account)
        {
            var setTimeoutWindow = new SetTimeoutWindow(account.Timeout);
            setTimeoutWindow.ShowDialog();

            if (setTimeoutWindow.timeout != null && setTimeoutWindow.timeout != new DateTime())
            {
                account.Timeout = setTimeoutWindow.timeout;
                SerializeAccounts();
            }
        }

        private void AccountButtonClearTimeout_Click(Account account)
        {
            account.Timeout = null;
            SerializeAccounts();
        }

        private void AccountButtonExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                Account account = (Account)btn.Tag;
                int hash = account.GetHashCode();

                // Check if this index has already been added.
                // Remove if it is, add if it isn't.
                if (actionAccounts.ContainsKey(hash))
                {
                    actionAccounts.Remove(hash);
                    btn.Opacity = 1;
                }
                else
                {
                    actionAccounts.Add(hash, account);
                    btn.Opacity = 0.5;
                }
            }
        }

        public async Task ReloadAccount_ClickAsync(Account account)
        {
            await ReloadAccount(account);
            SerializeAccounts();
            MessageBox.Show("Done!");
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsDialog = new SettingsWindow();
            settingsDialog.ShowDialog();

            settings.User.AccountsPerRow = settingsDialog.AccountsPerRow;

            string previousPass = ePassword;

            if (settingsDialog.Decrypt)
            {
                AccountUtils.Serialize(accounts);
                ePassword = "";
            }
            else if (!string.IsNullOrEmpty(settingsDialog.Password))
            {
                ePassword = settingsDialog.Password;

                if (previousPass != ePassword)
                {
                    AccountUtils.PasswordSerialize(accounts, ePassword);
                }
            }

            LoadSettings();
            RefreshWindow(dataFile);
        }

        private void DeleteBannedAccounts_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show("Are you sure you want to delete all banned accounts?" +
                "\nThis action is perminant and cannot be undone!", "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                List<Account> accountsToDelete = new List<Account>();

                foreach (Account account in accounts)
                {
                    if (account.NumberOfVACBans > 0 || account.NumberOfGameBans > 0)
                    {
                        accountsToDelete.Add(account);
                    }
                }

                foreach (Account account in accountsToDelete)
                {
                    accounts.Remove(account);
                }

                SerializeAccounts();
            }
        }

        private void GitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(repositoryUrl);
        }

        private async void Ver_Click(object sender, RoutedEventArgs e)
        {
            UpdateResponse response = await UpdateHelper.CheckForUpdate(updateCheckUrl);

            switch (response)
            {
                case UpdateResponse.NoUpdate:
                    MessageBox.Show(Process.GetCurrentProcess().ProcessName + " is up to date!");
                    break;

                case UpdateResponse.Update:
                    await UpdateHelper.StartUpdate(updateCheckUrl, releasesUrl);
                    Close();
                    return;
            }
        }

        private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
        {
            RefreshWindow(dataFile);
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SortUsername_Click(object sender, RoutedEventArgs e)
        {
            SortAccounts(SortType.Username);
        }

        private void SortAlias_Click(object sender, RoutedEventArgs e)
        {
            SortAccounts(SortType.Alias);
        }

        private void SortBanned_Click(object sender, RoutedEventArgs e)
        {
            SortAccounts(SortType.Banned);
        }

        private void ShuffleAccounts_Click(object sender, RoutedEventArgs e)
        {
            SortAccounts(SortType.Random);
        }

        private void SortLastLogin_Click(object sender, RoutedEventArgs e)
        {
            SortAccounts(SortType.LastLogin);
        }

        private void ImportFromFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            AccountUtils.ImportAccountFile();
            RefreshWindow(dataFile);
        }

        private void ExportAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            AccountUtils.ExportAccountFile();
        }

        private void ExportSelectedAccount_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                ExportAccount((Account)btn.Tag);
            }
        }

        private async void ReloadAccounts_Click(object sender, RoutedEventArgs e)
        {
            await ReloadAccountsAsync();
        }

        private void ShowWindowButton_Click(object sender, RoutedEventArgs e)
        {
            ShowMainWindow();
        }

        private void TaskbarIcon_TrayLeftMouseUp(object sender, RoutedEventArgs e)
        {
            ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
        }

        private void CopyUsernameToClipboard(Account account)
        {
            try
            {
                Clipboard.SetText(account.Name);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        }

        private void CopyPasswordToClipboard(Account account)
        {
            try
            {
                string password = StringCipher.Decrypt(account.Password, eKey);
                Clipboard.SetText(password);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        }

        private void CopySteamIdToClipboard(Account account)
        {
            try
            {
                Clipboard.SetText(account.SteamId);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        }

        private void CopyProfileUrlToClipboard(Account account)
        {
            try
            {
                Clipboard.SetText(account.ProfUrl);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message);
            }
        }

        private void LoginAllMissingItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult messageBoxResult = MessageBox.Show(
                "You are about to start the automatic login sequence for all " +
                "accounts that do not currently have an associated Steam Id. " +
                "This will generate a Steam Id in the local vdf files for these " +
                "accounts to be read by SAM.\n\n" +
                "You can cancel this process at any time with ESC.\n\n" +
                "This may take some time depending on the number of accounts. " +
                "Are you sure you want to login all accounts missing a Steam Id?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (messageBoxResult == MessageBoxResult.Yes)
            {
                FileMenu.IsEnabled = false;
                AccountsDataGrid.IsEnabled = false;
                buttonGrid.IsEnabled = false;
                AddButtonGrid.IsEnabled = false;
                TaskBarIconLoginContextMenu.IsEnabled = false;

                loginAllSequence = true;

                InterceptKeys.OnKeyDown += new System.Windows.Forms.KeyEventHandler(EscKeyDown);
                InterceptKeys.Start();

                Task.Run(() => LoginAllMissing());
            }
        }

        #endregion

        private void ContextMenu_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (exporting)
            {
                GenerateAltActionContextMenu(AltActionType.EXPORTING).IsOpen = true;
                e.Handled = true;
            }
            else if (deleting)
            {
                GenerateAltActionContextMenu(AltActionType.DELETING).IsOpen = true;
                e.Handled = true;
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            switch (WindowState)
            {
                case WindowState.Maximized:
                    break;
                case WindowState.Minimized:
                    if (settings.User.MinimizeToTray)
                    {
                        Visibility = Visibility.Hidden;
                        ShowInTaskbar = false;
                    }
                    break;
                case WindowState.Normal:
                    Visibility = Visibility.Visible;
                    ShowInTaskbar = true;
                    break;
            }
        }

        private void ImportDelimitedTextMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var importDelimitedWindow = new ImportDelimited(eKey);
            importDelimitedWindow.ShowDialog();

            RefreshWindow(dataFile);
        }

        private void ExposeCredentialsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult messageBoxResult = MessageBox.Show("Are you sure you want to expose all account credentials in plain text?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (messageBoxResult == MessageBoxResult.No || (IsPasswordProtected() && !VerifyPassword()))
            {
                return;
            }

            var exposedCredentialsWindow = new ExposedInfoWindow(accounts, eKey);
            exposedCredentialsWindow.ShowDialog();
        }

        private bool IsPasswordProtected()
        {
            if (settings.User.PasswordProtect)
            {
                return true;
            }
            else
            {
                try
                {
                    if (!File.Exists(dataFile))
                    {
                        return false;
                    }
                    else
                    {
                        string[] lines = File.ReadAllLines(dataFile);
                        if (lines.Length == 0 || lines.Length > 1)
                        {
                            return false;
                        }
                    }
                    AccountUtils.Deserialize(dataFile);
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }

        void TimeoutTimer_Tick(Account account, TextBlock timeoutLabel, System.Timers.Timer timeoutTimer)
        {
            var timeLeft = account.Timeout - DateTime.Now;

            if (timeLeft.Value.CompareTo(TimeSpan.Zero) <= 0)
            {
                timeoutTimer.Stop();
                timeoutTimer.Dispose();

                timeoutLabel.Visibility = Visibility.Hidden;
                AccountButtonClearTimeout_Click(account);
            }
            else
            {
                timeoutLabel.Text = AccountUtils.FormatTimespanString(timeLeft.Value);
                timeoutLabel.Visibility = Visibility.Visible;
            }
        }

        void TimeoutTimer_Tick(Account account, System.Timers.Timer timeoutTimer)
        {
            var timeLeft = account.Timeout - DateTime.Now;

            if (timeLeft.Value.CompareTo(TimeSpan.Zero) <= 0)
            {
                timeoutTimer.Stop();
                timeoutTimer.Dispose();

                account.TimeoutTimeLeft = null;
                AccountButtonClearTimeout_Click(account);
            }
            else
            {
                account.TimeoutTimeLeft = AccountUtils.FormatTimespanString(timeLeft.Value);
            }
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (!isLoadingSettings && settings.File != null && IsInBounds())
            {
                settings.File.Write(SAMSettings.WINDOW_LEFT, Left.ToString(), SAMSettings.SECTION_LOCATION);
                settings.File.Write(SAMSettings.WINDOW_TOP, Top.ToString(), SAMSettings.SECTION_LOCATION);
            }
        }

        private void MetroWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!isLoadingSettings && settings.File != null && settings.User.ListView)
            {
                settings.User.ListViewHeight = Height;
                settings.User.ListViewWidth = Width;

                settings.File.Write(SAMSettings.LIST_VIEW_HEIGHT, Height.ToString(), SAMSettings.SECTION_LOCATION);
                settings.File.Write(SAMSettings.LIST_VIEW_WIDTH, Width.ToString(), SAMSettings.SECTION_LOCATION);
            }
        }

        private void SetMainScrollViewerBarsVisibility(ScrollBarVisibility visibility)
        {
            // 始终禁用滚动条，避免显示滚动条
            MainScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            MainScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        private void SetWindowSettingsIntoScreenArea()
        {
            if (!IsInBounds())
            {
                SetWindowToCenter();
            }
        }

        private bool IsInBounds()
        {
            foreach (System.Windows.Forms.Screen scrn in System.Windows.Forms.Screen.AllScreens)
            {
                if (scrn.Bounds.Contains((int)Left, (int)Top))
                {
                    return true;
                }
            }

            return false;
        }

        private void SetWindowToCenter()
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            Left = (screenWidth / 2) - (Width / 2);
            Top = (screenHeight / 2) - (Height / 2);
        }

        #region Account Button State Handling

        private void ExportSelectedMenuItem_Click(object sender, RoutedEventArgs e)
        {
            exporting = true;
            FileMenuItem.IsEnabled = false;
            EditMenuItem.IsEnabled = false;

            actionAccounts = new Dictionary<int, Account>();

            if (settings.User.ListView)
            {
                AccountsDataGrid.SelectionMode = DataGridSelectionMode.Extended;
                Application.Current.Resources["AccountGridActionHighlightColor"] = Brushes.Green;
            }
            else
            {
                AddButton.Visibility = Visibility.Hidden;
                ExportButton.Visibility = Visibility.Visible;
                CancelExportButton.Visibility = Visibility.Visible;

                IEnumerable<Grid> buttonGridCollection = buttonGrid.Children.OfType<Grid>();

                foreach (Grid accountButtonGrid in buttonGridCollection)
                {
                    Button accountButton = accountButtonGrid.Children.OfType<Button>().FirstOrDefault();

                    accountButton.Style = (Style)Resources["ExportButtonStyle"];
                    accountButton.Click -= new RoutedEventHandler(AccountButton_Click);
                    accountButton.Click += new RoutedEventHandler(AccountButtonExport_Click);
                    accountButton.PreviewMouseLeftButtonDown -= new MouseButtonEventHandler(AccountButton_MouseDown);
                    accountButton.PreviewMouseLeftButtonUp -= new MouseButtonEventHandler(AccountButton_MouseUp);
                    accountButton.MouseLeave -= new MouseEventHandler(AccountButton_MouseLeave);
                }
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            ExportSelectedAccounts();
        }

        private void ExportSelectedAccounts()
        {
            if (settings.User.ListView)
            {
                for (int i = 0; i < AccountsDataGrid.SelectedItems.Count; i++)
                {
                    Account account = AccountsDataGrid.SelectedItems[i] as Account;
                    actionAccounts.Add(account.GetHashCode(), account);
                }
            }

            if (actionAccounts.Count > 0)
            {
                AccountUtils.ExportSelectedAccounts(actionAccounts.Values.ToList());
            }
            else
            {
                MessageBox.Show("No accounts selected to export!");
            }

            ResetFromExportOrDelete();
        }

        private void CancelExportButton_Click(object sender, RoutedEventArgs e)
        {
            ResetFromExportOrDelete();
        }

        private void DeleteAllAccountsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (accounts.Count > 0)
            {
                MessageBoxResult messageBoxResult = MessageBox.Show("Are you sure you want to delete all accounts?\nThis action will perminantly delete the account data file.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (messageBoxResult == MessageBoxResult.Yes)
                {
                    if ((IsPasswordProtected() && VerifyPassword()) || !IsPasswordProtected())
                    {
                        try
                        {
                            File.Delete(dataFile);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(ex.Message);
                        }

                        RefreshWindow(dataFile);
                    }
                }
            }
        }

        private void DeleteSelectedMenuItem_Click(object sender, RoutedEventArgs e)
        {
            deleting = true;
            actionAccounts = new Dictionary<int, Account>();

            FileMenuItem.IsEnabled = false;
            EditMenuItem.IsEnabled = false;

            if (settings.User.ListView)
            {
                AccountsDataGrid.SelectionMode = DataGridSelectionMode.Extended;
                Application.Current.Resources["AccountGridActionHighlightColor"] = Brushes.Red;
            }
            else
            {
                AddButton.Visibility = Visibility.Hidden;
                DeleteButton.Visibility = Visibility.Visible;
                CancelExportButton.Visibility = Visibility.Visible;

                IEnumerable<Grid> buttonGridCollection = buttonGrid.Children.OfType<Grid>();

                foreach (Grid accountButtonGrid in buttonGridCollection)
                {
                    Button accountButton = accountButtonGrid.Children.OfType<Button>().FirstOrDefault();

                    accountButton.Style = (Style)Resources["DeleteButtonStyle"];
                    accountButton.Click -= new RoutedEventHandler(AccountButton_Click);
                    accountButton.Click += new RoutedEventHandler(AccountButtonDelete_Click);
                    accountButton.PreviewMouseLeftButtonDown -= new MouseButtonEventHandler(AccountButton_MouseDown);
                    accountButton.PreviewMouseLeftButtonUp -= new MouseButtonEventHandler(AccountButton_MouseUp);
                    accountButton.MouseLeave -= new MouseEventHandler(AccountButton_MouseLeave);
                }
            }
        }

        private void AccountButtonDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                Account account = (Account)btn.Tag;
                int hash = account.GetHashCode();

                // Check if this index has already been added.
                // Remove if it is, add if it isn't.
                if (actionAccounts.ContainsKey(hash))
                {
                    actionAccounts.Remove(hash);
                    btn.Opacity = 1;
                }
                else
                {
                    actionAccounts.Add(hash, account);
                    btn.Opacity = 0.5;
                }
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedAccounts();
        }

        private void DeleteSelectedAccounts()
        {
            if (settings.User.ListView)
            {
                for (int i = 0; i < AccountsDataGrid.SelectedItems.Count; i++)
                {
                    Account account = AccountsDataGrid.SelectedItems[i] as Account;
                    actionAccounts.Add(account.GetHashCode(), account);
                }
            }

            if (actionAccounts.Count > 0)
            {
                MessageBoxResult messageBoxResult = MessageBox.Show("Are you sure you want to delete the selected accounts?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (messageBoxResult == MessageBoxResult.Yes)
                {
                    foreach (Account account in actionAccounts.Values.ToList())
                    {
                        accounts.Remove(account);
                    }

                    SerializeAccounts();
                }
            }
            else
            {
                MessageBox.Show("No accounts selected!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ResetFromExportOrDelete();
        }

        private void ResetFromExportOrDelete()
        {
            FileMenuItem.IsEnabled = true;
            EditMenuItem.IsEnabled = true;

            if (settings.User.ListView)
            {
                AccountsDataGrid.SelectionMode = DataGridSelectionMode.Single;
            }
            else
            {
                if (!settings.User.HideAddButton)
                {
                    AddButton.Visibility = Visibility.Visible;
                }

                DeleteButton.Visibility = Visibility.Hidden;
                ExportButton.Visibility = Visibility.Hidden;
                CancelExportButton.Visibility = Visibility.Hidden;

                IEnumerable<Grid> buttonGridCollection = buttonGrid.Children.OfType<Grid>();

                foreach (Grid accountButtonGrid in buttonGridCollection)
                {
                    Button accountButton = accountButtonGrid.Children.OfType<Button>().FirstOrDefault();

                    accountButton.Style = (Style)Resources["SAMButtonStyle"];
                    accountButton.Click -= new RoutedEventHandler(AccountButtonExport_Click);
                    accountButton.Click -= new RoutedEventHandler(AccountButtonDelete_Click);
                    accountButton.Click += new RoutedEventHandler(AccountButton_Click);
                    accountButton.PreviewMouseLeftButtonDown += new MouseButtonEventHandler(AccountButton_MouseDown);
                    accountButton.PreviewMouseLeftButtonUp += new MouseButtonEventHandler(AccountButton_MouseUp);
                    accountButton.MouseLeave += new MouseEventHandler(AccountButton_MouseLeave);

                    accountButton.Opacity = 1;
                }
            }

            deleting = false;
            exporting = false;
        }

        private void AccountsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AccountsDataGrid.SelectedItem != null && !deleting)
            {
                Account account = AccountsDataGrid.SelectedItem as Account;
                Login(account);
            }
        }

        private void AccountsDataGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (exporting)
            {
                AccountsDataGrid.ContextMenu = GenerateAltActionContextMenu(AltActionType.EXPORTING);
            }
            else if (deleting)
            {
                AccountsDataGrid.ContextMenu = GenerateAltActionContextMenu(AltActionType.DELETING);
            }
            else if (AccountsDataGrid.SelectedItem != null)
            {
                Account account = AccountsDataGrid.SelectedItem as Account;
                AccountsDataGrid.ContextMenu = GenerateAccountContextMenu(account);
            }
        }

        private void AccountsDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject DepObject = (DependencyObject)e.OriginalSource;

            while ((DepObject != null) && !(DepObject is DataGridColumnHeader) && !(DepObject is DataGridRow))
            {
                DepObject = VisualTreeHelper.GetParent(DepObject);
            }

            if (DepObject == null || DepObject is DataGridColumnHeader)
            {
                AccountsDataGrid.ContextMenu = null;
            }
        }

        #endregion

        private void AccountsDataGrid_ColumnReordered(object sender, DataGridColumnEventArgs e)
        {
            foreach (DataGridColumn column in AccountsDataGrid.Columns)
            {
                settings.File.Write(settings.ListViewColumns[column.Header.ToString()], column.DisplayIndex.ToString(), SAMSettings.SECTION_COLUMNS);
            }
        }

        private void AccountsDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Column.Header.ToString() == "离线模式")
            {
                // 保存账号信息
                SerializeAccounts();
            }
        }

        private void MainScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer scv = (ScrollViewer)sender;
            scv.ScrollToVerticalOffset(scv.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private void LoginAllMissing()
        {
            // Auto login all accounts missing steamId.
            foreach (Account account in accounts)
            {
                if (loginAllCancelled)
                {
                    StopLoginAllMissing();
                    return;
                }

                if (string.IsNullOrEmpty(account.SteamId))
                {
                    Login(account);

                    // Wait and check if full steam client window is open.
                    WindowUtils.WaitForSteamClientWindow();
                }
            }

            StopLoginAllMissing();

            MessageBox.Show("Done!");
        }

        private void StopLoginAllMissing()
        {
            InterceptKeys.Stop();
            InterceptKeys.OnKeyDown -= EscKeyDown;

            ShutdownSteam();

            Dispatcher.Invoke(() =>
            {
                ReloadAccountsAsync();

                FileMenu.IsEnabled = true;
                AccountsDataGrid.IsEnabled = true;
                buttonGrid.IsEnabled = true;
                AddButtonGrid.IsEnabled = true;
                TaskBarIconLoginContextMenu.IsEnabled = true;
                loginAllSequence = false;
                loginAllCancelled = false;
            });

            ResetWindowTitle();
            steamUpdateDetected = false;
        }

        private void EscKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (e.KeyCode == System.Windows.Forms.Keys.Escape)
            {
                // Prompt to cancel auto login process.
                MessageBoxResult messageBoxResult = MessageBox.Show(
                "Cancel Login All Sequence?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (messageBoxResult == MessageBoxResult.Yes)
                {
                    Dispatcher.Invoke(() =>
                    {
                        loginAllCancelled = true;
                    });

                    WindowUtils.CancelLoginAll();
                }
            }
        }

        private void ShutdownSteam()
        {
            // Shutdown Steam process via command if it is already open.
            ProcessStartInfo stopInfo = new ProcessStartInfo
            {
                FileName = settings.User.SteamPath + "steam.exe",
                WorkingDirectory = settings.User.SteamPath,
                Arguments = "-shutdown"
            };

            try
            {
                Process SteamProc = Process.GetProcessesByName("Steam").FirstOrDefault();
                Process[] WebClientProcs = Process.GetProcessesByName("steamwebhelper");
                if (SteamProc != null)
                {
                    Process.Start(stopInfo);
                    SteamProc.WaitForExit();

                    foreach (Process proc in WebClientProcs)
                    {
                        proc.WaitForExit();
                    }
                }
            }
            catch
            {
                Console.WriteLine("No steam process found or steam failed to shutdown.");
            }
        }

        private void AccountsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (loginThreads != null)
            {
                foreach (Thread thread in loginThreads)
                {
                    Console.WriteLine("Aborting thread...");
                    thread.Abort();
                }
            }

            TaskbarIcon.Dispose();
        }

        private void AccountsDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                if (AccountsDataGrid.SelectedItem != null)
                {
                    Account account = AccountsDataGrid.SelectedItem as Account;
                    Login(account);
                }

                e.Handled = true;
            }
        }

        private void SetWindowTitle(string title)
        {
            string newTitle = "SAM";

            if (title != null)
            {
                newTitle += " | " + title;
            }

            Dispatcher.Invoke(delegate () {
                MainGrid.IsEnabled = title == null;
                Title = newTitle;
            });
        }

        private void ResetWindowTitle()
        {
            SetWindowTitle(null);
        }

        private void ShowErrorMessage(string message)
        {
            ShowMessage("Error", message);
        }

        private void ShowMessage(string title, string message)
        {
            Dispatcher.Invoke(async delegate () {
                await this.ShowMessageAsync(title, message);
            });
        }
    }
}
