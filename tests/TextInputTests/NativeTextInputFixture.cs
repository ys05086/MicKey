using MicPtt.Input;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Provider;

/// <summary>
/// An opt-in fixture owns every window/provider it inspects. It never types,
/// accesses microphone state, reads another app's content, or creates a caret.
/// The native fixture uses an injected focus identity and reads only its own UIA
/// provider. Its invisible no-activate window never takes focus from the user.
/// </summary>
internal static class NativeTextInputFixture
{
    public static void Run(Action<bool, string> check)
    {
        Exception? failure = null;
        var ui = new Thread(() =>
        {
            try { RunUi(check); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        if (!ui.Join(TimeSpan.FromSeconds(25))) throw new TimeoutException("Native text-input fixture timed out.");
        if (failure is not null) throw new InvalidOperationException("Native text-input fixture failed.", failure);
    }

    private static void RunUi(Action<bool, string> check)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var form = new NoActivateForm
        {
            Text = "MicKey · 입력 감지 테스트",
            ClientSize = new Size(360, 110),
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
            Opacity = 0
        };
        using var surface = new MetadataControl { Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 33, 44) };
        form.Controls.Add(surface);
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            TextInputDetector? detector = null;
            try
            {
                var originalForeground = TextInputDetector.GetForegroundInfo();
                nint fixtureWindow = form.Handle;
                nint fixtureControl = surface.Handle;
                surface.Provider.HasFocus = true;
                check(fixtureControl != nint.Zero, "Custom metadata provider owns a real native HWND.");
                check(NoCaretOnCurrentThread(), "Fixture has no native caret.");
                detector = new TextInputDetector(() => new ForegroundWindowInfo(fixtureWindow, Environment.ProcessId),
                    () => AutomationElement.FromHandle(fixtureControl), useNativeFallback: false, subscribeFocusEvents: false);

                await Expect(detector, form.Handle, false, "Generic game/browser pane", check);
                nint unchangedWindow = form.Handle;
                surface.SetMetadata(ControlType.Custom.Id, hasValue: true, readOnly: false, password: false);
                await Expect(detector, form.Handle, true, "Custom writable ValuePattern input", check);
                surface.SetMetadata(ControlType.Edit.Id, hasValue: true, readOnly: false, password: false);
                await Expect(detector, form.Handle, true, "Writable UIA input without native Edit class or caret", check);
                check(form.Handle == unchangedWindow && NoCaretOnCurrentThread(), "Typing detection changed inside the same top-level HWND without a caret.");

                surface.SetMetadata(ControlType.Edit.Id, hasValue: true, readOnly: true, password: false);
                await Expect(detector, form.Handle, false, "Read-only UIA input", check);
                surface.SetMetadata(ControlType.Edit.Id, hasValue: false, readOnly: false, password: true);
                await Expect(detector, form.Handle, true, "Password metadata without retrieving its value", check);
                surface.SetMetadata(ControlType.Pane.Id, hasValue: false, readOnly: true, password: false);
                await Expect(detector, form.Handle, false, "Return to non-editable game/browser pane", check);
                check(surface.Provider.ValueReads == 0, "Detector never requested a value or password string.");
                check(surface.Provider.NameReads == 0, "Detector never requested a control name.");
                check(TextInputDetector.GetForegroundInfo() == originalForeground, "Fixture did not change real foreground focus.");

                var readTimer = Stopwatch.StartNew();
                for (var i = 0; i < 100; i++) _ = detector.Snapshot;
                readTimer.Stop();
                check(readTimer.ElapsedMilliseconds < 100, "100 cached snapshot reads remain nonblocking.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (detector is not null)
                {
                    var disposeTimer = Stopwatch.StartNew();
                    detector.Dispose();
                    disposeTimer.Stop();
                    try { check(disposeTimer.ElapsedMilliseconds < 350, "Detector shutdown is bounded."); }
                    catch (Exception ex) { failure ??= ex; }
                }
                form.Close();
            }
        };
        Application.Run(form);
        if (failure is not null) throw failure;
    }

    private static async Task Expect(TextInputDetector detector, nint expectedWindow, bool text, string label, Action<bool, string> check)
    {
        long requestedAt = Stopwatch.GetTimestamp();
        detector.RequestRefresh();
        var timeout = Stopwatch.StartNew();
        TextInputSnapshot last = detector.Snapshot;
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(35); // Keep the fixture's UI message loop responsive to UIA.
            last = detector.Snapshot;
            if (last.WindowHandle == expectedWindow && last.ProcessId == Environment.ProcessId
                && !last.IsUnknown && last.IsTextInput == text && last.CheckedAtTicks >= requestedAt)
            {
                check(true, label);
                return;
            }
        }
        throw new InvalidOperationException($"{label}: expected text={text}; observed text={last.IsTextInput}, unknown={last.IsUnknown}, matchingPid={last.ProcessId == Environment.ProcessId}.");
    }

    private static bool NoCaretOnCurrentThread()
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(GetCurrentThreadId(), ref info) && info.Caret == nint.Zero;
    }

    private sealed class MetadataControl : Control
    {
        public MetadataProvider Provider { get; }

        public MetadataControl()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Provider = new MetadataProvider();
        }

        public void SetMetadata(int role, bool hasValue, bool readOnly, bool password)
        {
            Provider.Role = role;
            Provider.HasValue = hasValue;
            Provider.ReadOnly = readOnly;
            Provider.Password = password;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Provider.Window = Handle;
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Provider.HasFocus = true;
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Provider.HasFocus = false;
            base.OnLostFocus(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x3D && unchecked((int)message.LParam) == AutomationInteropProvider.RootObjectId)
            {
                message.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, message.WParam, message.LParam, Provider);
                return;
            }
            base.WndProc(ref message);
        }
    }

    private sealed class NoActivateForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000;
                return parameters;
            }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class MetadataProvider : IRawElementProviderSimple, IValueProvider
    {
        public nint Window;
        public volatile int Role = ControlType.Pane.Id;
        public volatile bool HasFocus;
        public volatile bool HasValue;
        public volatile bool ReadOnly;
        public volatile bool Password;
        public int ValueReads;
        public int NameReads;

        public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;
        public IRawElementProviderSimple HostRawElementProvider => AutomationInteropProvider.HostProviderFromHandle(Window);
        public object? GetPatternProvider(int patternId) => patternId == ValuePatternIdentifiers.Pattern.Id && HasValue ? this : null;

        public object? GetPropertyValue(int propertyId)
        {
            if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id) return Role;
            if (propertyId == AutomationElementIdentifiers.ProcessIdProperty.Id) return Environment.ProcessId;
            if (propertyId == AutomationElementIdentifiers.NativeWindowHandleProperty.Id) return unchecked((int)Window);
            if (propertyId == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id) return true;
            if (propertyId == AutomationElementIdentifiers.HasKeyboardFocusProperty.Id) return HasFocus;
            if (propertyId == AutomationElementIdentifiers.IsEnabledProperty.Id) return true;
            if (propertyId == AutomationElementIdentifiers.IsPasswordProperty.Id) return Password;
            if (propertyId == AutomationElementIdentifiers.IsValuePatternAvailableProperty.Id) return HasValue;
            if (propertyId == AutomationElementIdentifiers.IsTextPatternAvailableProperty.Id) return false;
            if (propertyId == AutomationElementIdentifiers.IsControlElementProperty.Id) return true;
            if (propertyId == AutomationElementIdentifiers.IsContentElementProperty.Id) return true;
            if (propertyId == ValuePatternIdentifiers.IsReadOnlyProperty.Id) return HasValue ? ReadOnly : null;
            if (propertyId == AutomationElementIdentifiers.NameProperty.Id)
            {
                Interlocked.Increment(ref NameReads);
                return "";
            }
            if (propertyId == ValuePatternIdentifiers.ValueProperty.Id)
            {
                Interlocked.Increment(ref ValueReads);
                return "";
            }
            return null;
        }

        bool IValueProvider.IsReadOnly => ReadOnly;
        string IValueProvider.Value
        {
            get { Interlocked.Increment(ref ValueReads); return ""; }
        }
        void IValueProvider.SetValue(string value) => throw new InvalidOperationException("Fixture is metadata-only.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetCurrentThreadId();
}
