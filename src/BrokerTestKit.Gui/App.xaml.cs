using System;
using System.Windows;

namespace BrokerTestKit.Gui
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // An unhandled exception on a background thread would otherwise kill the app with no
            // explanation - and most of the interesting failures here (licence, RTI, FOM) surface
            // as exceptions the user needs to READ.
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(args.Exception.Message, "BrokerTestKit",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            base.OnStartup(e);
        }
    }
}
