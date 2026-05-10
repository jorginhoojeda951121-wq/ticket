using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace IRCTCTatkalBot.Helpers
{
    /// <summary>
    /// WPF clipboard often throws CLIPBRD_E_CANT_OPEN (0x800401D0) when another app holds the clipboard; retry with short delays.
    /// </summary>
    public static class ClipboardHelper
    {
        private const int ClipbrdECantOpen = unchecked((int)0x800401D0);

        /// <returns>true if text was placed on the clipboard.</returns>
        public static bool TrySetText(string text, int attempts = 35, int delayMs = 45)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    // Prefer SetText for plain Unicode text; some hosts handle it more reliably than SetDataObject alone.
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    return true;
                }
                catch (COMException ex) when (ex.HResult == ClipbrdECantOpen)
                {
                    Thread.Sleep(delayMs);
                }

                try
                {
                    // copy:false avoids deferred Flush issues when RDP/clipboard sync locks the clipboard.
                    Clipboard.SetDataObject(text, copy: false);
                    return true;
                }
                catch (COMException ex) when (ex.HResult == ClipbrdECantOpen)
                {
                    Thread.Sleep(delayMs);
                }
            }

            return false;
        }
    }
}
