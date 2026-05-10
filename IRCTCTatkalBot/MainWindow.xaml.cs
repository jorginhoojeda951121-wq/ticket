using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IRCTCTatkalBot.ViewModels;
using IRCTCTatkalBot.Views;
using IRCTCTatkalBot.Helpers;

namespace IRCTCTatkalBot
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            try
            {
                _vm = new MainViewModel();
                Logger.Info("MainWindow: ViewModel created");

                InitializeComponent();
                Logger.Info("MainWindow: Initialized XAML");

                DataContext = _vm;
                Logger.Info("MainWindow: DataContext set");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MainWindow.Constructor");
                MessageBox.Show($"MainWindow Error:\n{ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        private async void StartScheduledBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _vm.StartBookingAsync(immediate: false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "StartScheduledBtn_Click");
                MessageBox.Show(
                    DriverDiagnostics.BuildUserFacingMessage(ex),
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void StartNowBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _vm.StartBookingAsync(immediate: true);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "StartNowBtn_Click");
                MessageBox.Show(
                    DriverDiagnostics.BuildUserFacingMessage(ex),
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void TestChromeBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _vm.TestChromeDriverAsync();
                MessageBox.Show(
                    "Chrome and ChromeDriver started correctly and loaded about:blank.\r\n\r\n" +
                    "If full booking still fails, the issue is likely IRCTC or flow timing — not basic driver startup.",
                    "Chrome driver test",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TestChromeBtn_Click");
                MessageBox.Show(
                    DriverDiagnostics.BuildUserFacingMessage(ex),
                    "Chrome driver test failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _vm.StopBooking();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "StopBtn_Click");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddAccountBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new AddAccountDialog();
                if (dialog.ShowDialog() == true)
                {
                    _vm.AddAccount(
                        dialog.Username,
                        dialog.Password);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "AddAccountBtn_Click");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyLogAll_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.Logs.Count == 0) return;
            string text = string.Join(Environment.NewLine, _vm.Logs);
            if (ClipboardHelper.TrySetText(text)) return;
            Logger.Warning("CopyLogAll_Click: Clipboard busy (CLIPBRD_E_CANT_OPEN) after retries.");
            MessageBox.Show(
                "Windows could not open the clipboard (another app may be using it). Close remote clipboard tools or try again in a moment.",
                "Copy log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void CopyLogLine_Click(object sender, RoutedEventArgs e)
        {
            if (LiveLogList.SelectedItem is not string line) return;
            if (ClipboardHelper.TrySetText(line)) return;
            Logger.Warning("CopyLogLine_Click: Clipboard busy after retries.");
            MessageBox.Show(
                "Could not copy — clipboard is in use. Try again or select text after clicking the log line.",
                "Copy log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// Right-click often opens the menu without changing selection; select the row under the cursor so Copy line works.
        /// </summary>
        private void LiveLogList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not ListBox listBox) return;
            var hit = listBox.InputHitTest(Mouse.GetPosition(listBox)) as DependencyObject;
            for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is not ListBoxItem item) continue;
                item.IsSelected = true;
                listBox.SelectedItem = item.Content;
                break;
            }
        }

        private void LiveLogList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (LiveLogList.SelectedItem is not string line) return;
            e.Handled = true;
            if (!ClipboardHelper.TrySetText(line))
                Logger.Warning("LiveLogList Ctrl+C: clipboard busy after retries.");
        }

        private void JourneyDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (JourneyDatePicker.SelectedDate.HasValue)
            {
                _vm.JourneyDate = JourneyDatePicker.SelectedDate.Value;
            }
        }

    }
}
