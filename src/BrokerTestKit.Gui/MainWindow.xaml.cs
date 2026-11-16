using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using BrokerTestKit.Engine;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// The shell (PRP gui-redesign): sidebar, rig banner, and a content host that shows one screen
    /// at a time. Navigation is by <see cref="Page"/> name - nothing here is addressed by index (R1).
    ///
    /// The window holds NO RTI connection of its own; the agents are child processes owned by
    /// <see cref="RunService"/>, so a native fault kills an agent and not the app.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly AppServices _s = new AppServices();
        private NavTarget _current;
        private bool _syncingNav;

        public MainWindow()
        {
            // Everything lives beside the exe. Pull in an AppData workspace from an earlier build
            // first, so the move does not look like the registered FOMs vanished.
            _s.Workspace = new Workspace();
            var adopted = _s.Workspace.AdoptLegacyWorkspace();

            _s.Store = new Store(_s.Workspace);
            _s.Foms = new FomRegistry(_s.Store);
            _s.Settings = new AppSettings(_s.Store);
            _s.Connections = new ConnectionRegistry(_s.Store, _s.Foms);
            _s.Owner = this;
            _s.StatusSink = t => { if (Dispatcher.CheckAccess()) StatusText.Text = t; else Dispatcher.BeginInvoke(new Action(() => StatusText.Text = t)); };
            _s.NavigateTo = Navigate;

            InitializeComponent();

            try { MakEnvironment.Initialize(); }
            catch (Exception ex) { _s.Status("MAK environment: " + ex.Message); }

            _s.Foms.Load();
            _s.Foms.ImportLegacyTsv(Path.Combine(_s.Workspace.Root, "foms.tsv"));
            _s.Foms.SeedFromConfig(MakEnvironment.RtiConfigDir);
            _s.Connections.Load();
            _s.Settings.Load();
            _s.Settings.SeedFrom(MakEnvironment.LicenseFile);

            // T3: give every run from before pairs existed a pair and a test case. Once.
            string migrated = null;
            try
            {
                migrated = _s.Store.MigrateRunsToPairs(conn =>
                {
                    var c = _s.Connections.ByName(conn);
                    return c == null || string.IsNullOrEmpty(c.FomName) ? null : c.FomName;
                });
            }
            catch (Exception ex) { _s.Status("Could not group earlier runs into pairs: " + ex.Message); }

            _s.Runs = new RunService(_s, Dispatcher);
            _s.Runs.Changed += UpdateRunBox;
            _s.Rig = new RigMonitor(Dispatcher, () => _s.Settings.LicenceServer, AnyBrokerPair);
            _s.Rig.Changed += UpdateBanner;

            Loaded += (sender, e) =>
            {
                DarkTitleBar();
                _s.Rig.Start();
            };
            Closing += (sender, e) =>
            {
                if (_s.Runs.IsRunning &&
                    !Dialogs.Confirm(this, "A test is running",
                        "Closing stops the run after the current row. Rows that finished are already saved.",
                        "Stop and close"))
                { e.Cancel = true; return; }
                _s.Runs.Stop();
            };
            Closed += (sender, e) => { try { _s.Store.Dispose(); } catch { } };

            UpdateRunBox();
            Navigate(NavTarget.To(Page.Dashboard));
            _s.Status(_s.Foms.Items.Count + " FOM(s) registered · workspace " + _s.Workspace.Root +
                      (adopted == null ? "" : " · " + adopted) + (migrated == null ? "" : " · " + migrated));
        }

        private bool AnyBrokerPair()
        {
            try { foreach (var p in _s.Store.Pairs()) if (!p.SelfTest) return true; }
            catch { }
            return false;
        }

        // ------------------------------------------------------------- navigation --

        private void Navigate(NavTarget t)
        {
            var leaving = Host.Content as IPageView;
            if (leaving != null && !leaving.CanLeave()) { SyncNav(); return; }

            UIElement view;
            try { view = Create(t); }
            catch (Exception ex) { _s.Fail("Could not open the page", ex); SyncNav(); return; }

            _current = t;
            Host.Content = view;
            SyncNav();
        }

        private UIElement Create(NavTarget t)
        {
            switch (t.Page)
            {
                case Page.Dashboard: return new Views.DashboardView(_s);
                case Page.Pairs: return new Views.PairsView(_s);
                case Page.PairDetail: return new Views.PairDetailView(_s, t.PairId);
                case Page.PairEdit: return new Views.PairEditView(_s, t.PairId);
                case Page.TestCaseEdit: return new Views.TestCaseEditView(_s, t.PairId, t.TestCaseId, t);
                case Page.FomCompare: return new Views.FomCompareView(_s, t.PairId);
                case Page.TestCaseResult: return new Views.TestCaseResultView(_s, t.TestCaseId, t.RunId);
                case Page.AllResults: return new Views.AllResultsView(_s, t.FilterToPair ? t.PairId : (long?)null);
                case Page.CsvTemplate: return new Views.CsvTemplateView(_s);
                case Page.FomRegistry: return new Views.FomRegistryView(_s);
            }
            throw new ArgumentOutOfRangeException("page", t.Page.ToString());
        }

        /// <summary>Pairs stays highlighted on every pair-scoped page; Dashboard on All results.</summary>
        private void SyncNav()
        {
            if (_current == null) return;
            _syncingNav = true;
            try
            {
                switch (_current.Page)
                {
                    case Page.Dashboard:
                    case Page.AllResults:
                        MainNav.SelectedIndex = 0; ToolsNav.SelectedIndex = -1; break;
                    case Page.CsvTemplate:
                        MainNav.SelectedIndex = -1; ToolsNav.SelectedIndex = 0; break;
                    case Page.FomRegistry:
                        MainNav.SelectedIndex = -1; ToolsNav.SelectedIndex = 1; break;
                    default:
                        MainNav.SelectedIndex = 1; ToolsNav.SelectedIndex = -1; break;
                }
            }
            finally { _syncingNav = false; }
        }

        private void MainNavChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingNav || MainNav.SelectedIndex < 0) return;
            Navigate(NavTarget.To(MainNav.SelectedIndex == 0 ? Page.Dashboard : Page.Pairs));
        }

        private void ToolsNavChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingNav || ToolsNav.SelectedIndex < 0) return;
            Navigate(NavTarget.To(ToolsNav.SelectedIndex == 0 ? Page.CsvTemplate : Page.FomRegistry));
        }

        // ---------------------------------------------------------------- run box --

        private void UpdateRunBox()
        {
            var st = _s.Runs.Current;
            var live = st != null && st.Running;
            RunIdle.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            RunLive.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            RunBadgeText.Text = live ? "Running" : "Idle";
            RunBadgeText.Foreground = (System.Windows.Media.Brush)FindResource(live ? "AccentBackground" : "TextSecondary");
            RunBadge.Background = (System.Windows.Media.Brush)FindResource(live ? "RunningBadgeBackground" : "IdleBadgeBackground");
            RunBox.IsEnabled = live;
            if (!live) return;

            RunPair.Text = st.PairName;
            RunCase.Text = st.TestCaseName;
            RunProgress.Value = st.Fraction;
            RunRows.Text = st.Total == 0 ? "joining…" : st.ProgressText;
            RunPass.Text = st.PassText;
            RunFail.Text = st.FailText;
            RunNext.Visibility = _s.Runs.Queued.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RunNext.Text = _s.Runs.Queued.Count > 0 ? "Queued · " + _s.Runs.Queued[0] +
                (_s.Runs.Queued.Count > 1 ? " +" + (_s.Runs.Queued.Count - 1) : "") : "";
        }

        private void RunBoxClicked(object sender, RoutedEventArgs e)
        {
            var st = _s.Runs.Current;
            if (st == null || !st.Running) return;
            Navigate(new NavTarget { Page = Page.TestCaseResult, TestCaseId = st.TestCaseId, RunId = st.RunId });
        }

        // ----------------------------------------------------------------- banner --

        private void UpdateBanner()
        {
            var text = _s.Rig.Banner;
            BannerBox.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            BannerText.Text = text ?? "";
            BannerText.Foreground = (System.Windows.Media.Brush)FindResource(_s.Rig.BannerIsFault ? "TextPrimary" : "TextSecondary");
        }

        private void RecheckRig(object sender, RoutedEventArgs e) { _s.Rig.CheckAsync(); _s.Status("Checking the licence and the broker…"); }

        // ------------------------------------------------------------------ chrome --

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// Paint the OS caption dark to match. Attribute 20 is the current id, 19 the early-Win10
        /// one; both fail silently where unsupported, which is the right outcome.
        /// </summary>
        private void DarkTitleBar()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { }
        }
    }
}
