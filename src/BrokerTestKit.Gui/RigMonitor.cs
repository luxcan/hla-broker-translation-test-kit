using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Threading;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// The two things that make a run stall silently (PRP §3.8, R3), checked off the UI thread at
    /// start, before every run and every 60 s:
    ///   1. the MAK vendor daemon - `lmutil lmstat` must say `maklmgrd: UP`; lmgrd alone is not
    ///      enough, and vrx stalls at "Locking Mutex" without it;
    ///   2. VR-Exchange - vrx listens on TCP 5111 once licensed and started.
    /// "Could not check" is its own, milder state: never reported as healthy.
    /// </summary>
    public sealed class RigMonitor
    {
        public enum State { Unknown, Ok, Down, CannotCheck }

        public const string LmUtil = @"C:\MAK\MAKLicenseManager\lmutil.exe";
        public const int BrokerPort = 5111;

        private readonly Dispatcher _ui;
        private readonly Func<string> _licenceServer;
        private readonly Func<bool> _brokerMatters;
        private readonly DispatcherTimer _timer;
        private int _checking;

        public State Licence { get; private set; }
        public State Broker { get; private set; }
        public string LicenceDetail { get; private set; }

        /// <summary>Raised on the UI thread after every check.</summary>
        public event Action Changed;

        /// <param name="brokerMatters">False hides the broker warning, e.g. when every pair is a self-test.</param>
        public RigMonitor(Dispatcher ui, Func<string> licenceServer, Func<bool> brokerMatters)
        {
            _ui = ui;
            _licenceServer = licenceServer;
            _brokerMatters = brokerMatters;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _timer.Tick += (s, e) => CheckAsync();
        }

        public void Start() { CheckAsync(); _timer.Start(); }

        public void CheckAsync()
        {
            if (Interlocked.Exchange(ref _checking, 1) == 1) return;
            var server = _licenceServer();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string detail;
                var lic = CheckLicence(server, out detail);
                var brk = CheckBroker();
                _ui.BeginInvoke(new Action(() =>
                {
                    Licence = lic; LicenceDetail = detail; Broker = brk;
                    Interlocked.Exchange(ref _checking, 0);
                    var h = Changed; if (h != null) h();
                }));
            });
        }

        /// <summary>Synchronous form for the run worker's pre-flight. Never call on the UI thread.</summary>
        public string PreflightProblem(string server, bool needBroker)
        {
            string detail;
            if (CheckLicence(server, out detail) == State.Down) return LicenceMessage(server, detail);
            if (needBroker && CheckBroker() == State.Down) return BrokerMessage;
            return null;
        }

        /// <summary>The banner text, or null when nothing is wrong.</summary>
        public string Banner
        {
            get
            {
                if (Licence == State.Down) return LicenceMessage(_licenceServer(), LicenceDetail);
                if (Broker == State.Down && _brokerMatters()) return BrokerMessage;
                if (Licence == State.CannotCheck)
                    return "Can't check the MAK licence (" + (LicenceDetail ?? "lmutil.exe not found") +
                           "). Runs may stall at \"Locking Mutex\" if the vendor daemon is down.";
                return null;
            }
        }

        /// <summary>True when the banner is a real fault, not the milder "could not check".</summary>
        public bool BannerIsFault
        {
            get { return Licence == State.Down || (Broker == State.Down && _brokerMatters()); }
        }

        public const string BrokerMessage =
            "VR-Exchange is not running — nothing will reach B. Start vrx.exe -l BTK-Bridge.xml -G in the VR-Exchange bin64 folder.";

        /// <summary>Runs will stall; say why in the Engine's words (REQ-07): vendor daemon down vs no server at all.</summary>
        public static string LicenceMessage(string server, string detail)
        {
            var daemon = detail != null && detail.IndexOf("vendor daemon", StringComparison.OrdinalIgnoreCase) >= 0;
            return "Runs will stall — " + BrokerTestKit.Engine.LicenceDiagnosis.Advice(
                daemon ? BrokerTestKit.Engine.LicenceDiagnosis.State.Down : BrokerTestKit.Engine.LicenceDiagnosis.State.NoServer,
                server, detail);
        }

        /// <summary>The Engine's probe (shared with the CLI's JOIN-timeout diagnosis), mapped to this monitor's states.</summary>
        private static State CheckLicence(string server, out string detail)
        {
            var s = BrokerTestKit.Engine.LicenceDiagnosis.Probe(server, out detail);
            switch (s)
            {
                case BrokerTestKit.Engine.LicenceDiagnosis.State.Ok: return State.Ok;
                case BrokerTestKit.Engine.LicenceDiagnosis.State.CannotCheck: return State.CannotCheck;
                default: return State.Down;
            }
        }

        private static State CheckBroker()
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect("127.0.0.1", BrokerPort, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return State.Down;
                    c.EndConnect(ar);
                    return State.Ok;
                }
            }
            catch { return State.Down; }
        }
    }
}
