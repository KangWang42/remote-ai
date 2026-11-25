using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace RemoteCli {
static class DesktopDpiCheck {
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("shcore.dll")] static extern int GetScaleFactorForMonitor(IntPtr monitor, out int percent);

    [STAThread] static int Main(string[] args) {
        Application.EnableVisualStyles();
        var form = new DesktopConsole(args[0], Rectangle.Empty, FormWindowState.Normal, null, null, null, null, null);
        int result = 1;
        form.ShowInTaskbar = false;
        form.Opacity = 0;
        form.Shown += async (s, e) => {
            try {
                var web = form.Controls.OfType<WebView2>().Single();
                for (int i = 0; web.CoreWebView2 == null && i < 100; i++) await Task.Delay(100);
                if (web.CoreWebView2 == null) throw new Exception("WebView2 did not start");
                web.CoreWebView2.NavigateToString("<html><body>Remote AI DPI test</body></html>");
                var screens = Screen.AllScreens;
                foreach (var screen in screens.Concat(screens.Take(1))) {
                    form.Bounds = new Rectangle(screen.WorkingArea.Left + 10, screen.WorkingArea.Top + 10, 920, 600);
                    await Task.Delay(400);
                    int scale;
                    if (GetScaleFactorForMonitor(MonitorFromWindow(form.Handle, 2), out scale) != 0) throw new Exception("Unknown display scale");
                    var dpi = GetDpiForWindow(form.Handle);
                    var ratio = Double.Parse(await web.ExecuteScriptAsync("devicePixelRatio"), CultureInfo.InvariantCulture);
                    Console.WriteLine("{0} scale={1}% window={2} browser={3}", screen.DeviceName, scale, dpi, ratio);
                    if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(form.Handle), (IntPtr)(-4))
                            || Math.Abs(dpi - scale * .96) > 1 || Math.Abs(ratio - scale / 100.0) > .02)
                        throw new Exception("Window or browser did not follow the monitor DPI");
                }
                result = 0;
            } catch (Exception err) { Console.Error.WriteLine(err); }
            finally { form.ForceClose(); }
        };
        Application.Run(form);
        return result;
    }
}
}
