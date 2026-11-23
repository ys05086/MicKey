using MicPtt;
using MicPtt.UI;
using System.Runtime.InteropServices;

/// <summary>
/// Optional desktop regression: dotnet run --project tests/OverlayTests -- --native
/// Uses a real ShowDialog loop, with no simulated clicks or other application input.
/// </summary>
internal static class NativeOverlayTests
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunOnUiThread(check); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Native overlay regression timed out.");
        if (failure is not null) throw new InvalidOperationException("Native overlay regression failed.", failure);
    }

    private static void RunOnUiThread(Action<bool, string> check)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var owner = new Form { Text = "MicKey overlay test", ShowInTaskbar = false, Opacity = 0 };
        owner.Show();
        using var overlay = new MicrophoneOverlay();
        overlay.ApplySettings(new OverlaySettings { Enabled = true, ShowWhenInactive = true });
        check(overlay.Visible && IsWindowEnabled(overlay.Handle), "Native overlay starts visible and enabled.");

        for (var iteration = 0; iteration < 2; iteration++)
        {
            using var modal = new Form { Text = "MicKey modal overlay test", ShowInTaskbar = false, Opacity = 0 };
            Exception? callbackFailure = null;
            modal.Shown += (_, _) => modal.BeginInvoke((Action)(() =>
            {
                try
                {
                    check(!IsWindowEnabled(overlay.Handle), "ShowDialog disables an already-visible unowned overlay HWND.");
                    check(!IsWindowEnabled(owner.Handle), "ShowDialog disables its owner.");
                    nint previousForeground = GetForegroundWindow();
                    overlay.SetEditing(true);
                    check(IsWindowEnabled(overlay.Handle), "Position editing re-enables the overlay HWND.");
                    check(!IsWindowEnabled(owner.Handle), "Position editing keeps the modal owner disabled.");
                    check(GetForegroundWindow() == previousForeground, "Position editing does not steal foreground focus.");
                    check((ReadExStyle(overlay.Handle) & 0x08000000) != 0, "Position editing preserves NOACTIVATE.");
                    check((ReadExStyle(overlay.Handle) & 0x20) == 0, "Position editing accepts mouse input.");

                    // A repeated editing request must also recover from native modal disabling.
                    EnableWindow(overlay.Handle, false);
                    overlay.SetEditing(true);
                    check(IsWindowEnabled(overlay.Handle), "Repeated editing activation recovers a disabled HWND.");
                    overlay.SetEditing(false);
                    check((ReadExStyle(overlay.Handle) & 0x20) != 0, "Leaving edit mode restores click-through.");
                }
                catch (Exception ex) { callbackFailure = ex; }
                finally { modal.Close(); }
            }));
            modal.ShowDialog(owner);
            if (callbackFailure is not null) throw callbackFailure;
            check(IsWindowEnabled(overlay.Handle), "Closing the modal leaves the overlay enabled.");
            check((ReadExStyle(overlay.Handle) & 0x20) != 0, "Closing the modal leaves the overlay click-through.");
        }
    }

    private static long ReadExStyle(nint window) => nint.Size == 8
        ? GetWindowLongPtrW(window, -20).ToInt64() : GetWindowLongW(window, -20);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(nint window, int index);
}
