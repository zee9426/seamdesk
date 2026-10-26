// Seamdesk's stand-ins for the parts of MWB the extracted code calls but that are not under test: logging, UI, settings,
// and the socket list. Only SkSend carries MWB behaviour: it numbers the package as Common.SkSend does
// (Core/Common.cs:254 at a9e1f0f20^) and sends it on the one test socket.
using System;
using System.IO;
using System.Threading;
using MouseWithoutBorders.Class;

namespace MouseWithoutBorders.Core
{
    internal static class Logger
    {
        internal static void Log(string message)
        {
        }

        internal static void Log(Exception e) => Console.Error.WriteLine($"mwb: {e.Message}");

        internal static void LogDebug(string message, params object[] args)
        {
        }
    }

    internal enum ToolTipIcon { None, Info, Warning, Error }

    internal static partial class Common
    {
        internal const int TOGGLE_ICONS_SIZE = 4;
        internal const int ICON_SMALL_CLIPBOARD = 0;
        internal const int ICON_BIG_CLIPBOARD = 1;

        internal static string MachineName { get; set; }

        internal static uint MachineIdValue { get; set; }

        internal static ID MachineID => (ID)MachineIdValue;

        internal static SocketStuff Sk { get; set; }

        internal static TcpSk Tcp { get; set; }

        internal static bool RunOnLogonDesktop => false;

        internal static bool RunOnScrSaverDesktop => false;

        internal static void SkSend(DATA data, uint? exceptDes, bool includeHandShakingSockets)
        {
            data.Id = Interlocked.Increment(ref Package.PackageID);
            Sk.TcpSend(Tcp, data);
        }

        internal static void SetToggleIcon(int[] toggleIcons)
        {
        }

        internal static void ShowToolTip(string tip, int timeOutInMilliseconds = 5000, ToolTipIcon icon = ToolTipIcon.Info, bool showBalloonTip = true, bool forceEvenIfHidingOldUI = false)
        {
        }
    }

    internal static partial class MachineStuff
    {
    }

    internal static partial class Clipboard
    {
        /// <summary>What MWB's SetClipboardData would have put on the clipboard.</summary>
        internal static DataObject Received { get; private set; }

        private static void SetDataObject(DataObject dataObject) => Received = dataObject;

        private static void SetImage(Image image) => throw new NotSupportedException("images are not sent by Seamdesk");
    }

    internal static class Receiver
    {
        internal static void ProcessPackage(DATA package, TcpSk tcp) => throw new InvalidOperationException($"unexpected {package.Type} inside clipboard data");
    }

    internal static class Thread
    {
        internal static System.Threading.Thread CurrentThread => System.Threading.Thread.CurrentThread;

        internal static void UpdateThreads(System.Threading.Thread thread)
        {
        }
    }

    internal static class Setting
    {
        internal static class Values
        {
            internal static bool ShowClipNetStatus => false;
        }
    }

    internal sealed class Image
    {
        internal static Image FromStream(Stream s) => throw new NotSupportedException();
    }

    internal static class DataFormats
    {
        internal const string Rtf = "Rich Text Format";
        internal const string Html = "HTML Format";
        internal const string UnicodeText = "UnicodeText";
    }

    internal sealed class DataObject
    {
        internal System.Collections.Generic.Dictionary<string, object> Data { get; } = [];

        internal void SetData(string format, object data) => Data[format] = data;
    }
}

namespace MouseWithoutBorders.Class
{
    internal partial class SocketStuff
    {
        internal object TcpSocketsLock = new();

        private void UpdateTcpSockets(TcpSk tcp, SocketStatus status)
        {
        }
    }
}
