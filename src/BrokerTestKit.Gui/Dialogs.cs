using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BrokerTestKit.Gui
{
    /// <summary>
    /// Themed replacements for <see cref="MessageBox"/>.
    ///
    /// The stock MessageBox is drawn by the OS and ignores the application's resources entirely,
    /// so in a dark-only app it flashes a white panel. These are ordinary WPF windows, so they pick
    /// up the Precision styles like everything else.
    /// </summary>
    public static class Dialogs
    {
        /// <summary>
        /// Ask before doing something destructive. Returns true only on an explicit confirm —
        /// Escape, the close button and the Cancel button all mean no.
        /// </summary>
        public static bool Confirm(Window owner, string title, string message,
                                   string confirmText = "Remove", bool destructive = true)
        {
            return Show(owner, title, message, confirmText, "Cancel", destructive);
        }

        /// <summary>Report something the user must read. Always returns false; there is only OK.</summary>
        public static void Inform(Window owner, string title, string message, bool isError = false)
        {
            Show(owner, title, message, "OK", null, isError);
        }

        private static bool Show(Window owner, string title, string message,
                                 string confirmText, string cancelText, bool emphasise)
        {
            var result = false;

            var win = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.Height,
                Width = 460,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner == null
                    ? WindowStartupLocation.CenterScreen
                    : WindowStartupLocation.CenterOwner,
                Owner = owner,
                ShowInTaskbar = false,
            };

            var root = new DockPanel { Margin = new Thickness(0) };

            var body = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = Brush(win, emphasise ? "ValidationError" : "TextPrimary"),
            });
            body.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
                Foreground = Brush(win, "TextSecondary"),
            });
            DockPanel.SetDock(body, Dock.Top);
            root.Children.Add(body);

            var bar = new Border
            {
                Background = Brush(win, "SurfaceAltBackground"),
                BorderBrush = Brush(win, "Divider"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 12, 16, 12),
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            if (!string.IsNullOrEmpty(cancelText))
            {
                var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 96 };
                cancel.Click += (s, e) => { result = false; win.Close(); };
                buttons.Children.Add(cancel);
            }

            var ok = new Button
            {
                Content = confirmText,
                MinWidth = 110,
                Margin = new Thickness(0),
                // Destructive confirms are NOT the default button: the safe choice keeps focus so
                // a reflexive Enter cannot delete anything.
                IsDefault = !emphasise || string.IsNullOrEmpty(cancelText),
                Style = (Style)win.TryFindResource("AccentButton"),
            };
            ok.Click += (s, e) => { result = true; win.Close(); };
            buttons.Children.Add(ok);

            bar.Child = buttons;
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);

            win.Content = root;
            win.Loaded += (s, e) =>
            {
                if (string.IsNullOrEmpty(cancelText)) ok.Focus();
                else ((Button)buttons.Children[0]).Focus();     // land on Cancel
            };
            win.ShowDialog();
            return result;
        }

        private static Brush Brush(FrameworkElement scope, string key)
        {
            var b = scope.TryFindResource(key) as Brush;
            return b ?? Brushes.Gray;
        }
    }
}
