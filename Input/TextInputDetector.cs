using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MicPtt.Input;

public sealed record TextInputSnapshot(
    bool IsTextInput,
    bool IsUnknown,
    int ProcessId,
    nint WindowHandle,
    long CheckedAtTicks)
{
    /// <summary>Monotonic age; CheckedAtTicks uses Stopwatch.GetTimestamp().</summary>
    public TimeSpan Age => Stopwatch.GetElapsedTime(CheckedAtTicks);
}

public readonly record struct ForegroundWindowInfo(nint WindowHandle, int ProcessId);

/// <summary>
/// Reads only accessibility roles and editability metadata, never names, values,
/// document text, passwords, or typed keys. UI Automation runs on a background MTA
/// thread; callers only read an immutable cache and fast foreground-window metadata.
/// </summary>
public sealed class TextInputDetector : IDisposable
{
    private const int PollMilliseconds = 75;
    private static readonly TimeSpan MaximumSampleAge = TimeSpan.FromMilliseconds(350);
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private readonly FocusSnapshotCache snapshots;
    private readonly Func<ForegroundWindowInfo> readForeground;
    private readonly Func<AutomationElement?> readFocusedElement;
    private readonly bool useNativeFallback;
    private readonly bool subscribeFocusEvents;
    private ProcessNameSnapshot? processName;
    private int processNameProbePending;
    private int stopped;

    public TextInputDetector() : this(GetForegroundInfo, static () => AutomationElement.FocusedElement, true, true)
    {
    }

    // The native fixture can restrict all reads to its own provider without
    // taking foreground focus away from the user's game or touching other apps.
    internal TextInputDetector(Func<ForegroundWindowInfo> readForeground,
        Func<AutomationElement?> readFocusedElement, bool useNativeFallback, bool subscribeFocusEvents)
    {
        this.readForeground = readForeground ?? throw new ArgumentNullException(nameof(readForeground));
        this.readFocusedElement = readFocusedElement ?? throw new ArgumentNullException(nameof(readFocusedElement));
        this.useNativeFallback = useNativeFallback;
        this.subscribeFocusEvents = subscribeFocusEvents;
        snapshots = new FocusSnapshotCache(readForeground());
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "MicKey.TextInputDetector"
        };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    /// <summary>
    /// Nonblocking. A result from a different foreground window/process, or one
    /// older than 350 ms, is unknown rather than reusing stale editable status.
    /// </summary>
    public TextInputSnapshot Snapshot
    {
        get
        {
            var foreground = readForeground();
            var current = snapshots.Current;
            if (current.WindowHandle != foreground.WindowHandle || current.ProcessId != foreground.ProcessId)
            {
                var unknown = Unknown(foreground);
                snapshots.Invalidate(foreground);
                RequestRefresh();
                return unknown;
            }
            if (current.Age > MaximumSampleAge)
            {
                RequestRefresh();
                return current with { IsUnknown = true };
            }
            return current;
        }
    }

    public void RequestRefresh()
    {
        if (Volatile.Read(ref stopped) != 0) return;
        try { wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Only HWND and PID; never retrieves window titles or UI Automation data.</summary>
    public static ForegroundWindowInfo GetForegroundInfo()
    {
        nint window = GetForegroundWindow();
        if (window == nint.Zero) return default;
        GetWindowThreadProcessId(window, out uint processId);
        return new ForegroundWindowInfo(window, unchecked((int)processId));
    }

    /// <summary>
    /// A separate nonblocking process-name cache, usable even if a UIA provider is
    /// unresponsive. Returns null until the current process has been identified.
    /// </summary>
    public string? GetForegroundProcessName()
    {
        int processId = readForeground().ProcessId;
        var current = Volatile.Read(ref processName);
        if (current?.ProcessId == processId) return current.Name;
        if (processId <= 0 || Volatile.Read(ref stopped) != 0) return null;
        if (Interlocked.CompareExchange(ref processNameProbePending, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string? name = null;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    name = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Inaccessible/exited processes are unknown, not an application-name guess.
                }
                finally
                {
                    if (Volatile.Read(ref stopped) == 0)
                        Volatile.Write(ref processName, new ProcessNameSnapshot(processId, name));
                    Volatile.Write(ref processNameProbePending, 0);
                }
            });
        }
        return null;
    }

    private void Run()
    {
        AutomationFocusChangedEventHandler focusChanged = (_, _) =>
        {
            if (Volatile.Read(ref stopped) != 0) return;
            snapshots.Invalidate(readForeground());
            RequestRefresh();
        };
        bool subscribed = false;
        bool subscriptionAttempted = false;
        try
        {
            var cache = CreateMetadataCache();
            while (Volatile.Read(ref stopped) == 0)
            {
                Probe(cache);
                if (subscribeFocusEvents && !subscriptionAttempted && Volatile.Read(ref stopped) == 0)
                {
                    subscriptionAttempted = true;
                    try
                    {
                        // Install and remove on this same background MTA thread.
                        Automation.AddAutomationFocusChangedEventHandler(focusChanged);
                        subscribed = true;
                    }
                    catch (Exception) { /* Polling remains available if events are unsupported. */ }
                }
                wake.WaitOne(PollMilliseconds);
            }
        }
        catch (Exception)
        {
            // The main UI and shortcut observer must survive a failing UIA provider.
            snapshots.Invalidate(readForeground());
        }
        finally
        {
            if (subscribed)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(focusChanged); }
                catch (Exception) { }
            }
            wake.Dispose();
        }
    }

    private void Probe(CacheRequest cache)
    {
        var ticket = snapshots.Capture();
        var foreground = readForeground();
        TextInputClassification classification = new(false, true);
        try
        {
            if (foreground.WindowHandle != nint.Zero && foreground.ProcessId > 0)
            {
                // Native edit classes supplement UIA; no caret presence is consulted.
                bool? nativeEditable = useNativeFallback ? ReadNativeEditableFocus(foreground) : null;
                if (nativeEditable == true)
                {
                    classification = new(true, false);
                }
                else
                {
                    AutomationElement? focused;
                    using (cache.Activate()) focused = readFocusedElement();
                    if (focused is not null && ReadCachedInt(focused, AutomationElement.ProcessIdProperty) == foreground.ProcessId)
                    {
                        var role = focused.GetCachedPropertyValue(AutomationElement.ControlTypeProperty, true) as ControlType;
                        bool password = ReadCachedBool(focused, AutomationElement.IsPasswordProperty) == true;
                        bool hasValue = ReadCachedBool(focused, AutomationElement.IsValuePatternAvailableProperty) == true;
                        bool? valueReadOnly = ReadCachedBool(focused, ValuePattern.IsReadOnlyProperty);
                        bool hasText = ReadCachedBool(focused, AutomationElement.IsTextPatternAvailableProperty) == true;
                        bool? textReadOnly = null;
                        if (hasText && !password && !(hasValue && valueReadOnly == false)
                            && focused.TryGetCachedPattern(TextPattern.Pattern, out var pattern) && pattern is TextPattern textPattern)
                        {
                            // Only this boolean formatting attribute is requested; never GetText.
                            object attribute = textPattern.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                            if (attribute is bool readOnly) textReadOnly = readOnly;
                        }

                        classification = TextInputClassificationPolicy.Classify(new FocusedInputMetadata(
                            role?.Id ?? 0,
                            ReadCachedBool(focused, AutomationElement.IsEnabledProperty),
                            ReadCachedBool(focused, AutomationElement.HasKeyboardFocusProperty),
                            ReadCachedBool(focused, AutomationElement.IsKeyboardFocusableProperty),
                            password,
                            hasValue,
                            valueReadOnly,
                            hasText,
                            textReadOnly));
                    }
                    else if (nativeEditable == false)
                    {
                        classification = new(false, false); // A known native read-only edit.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Missing/disappearing/inaccessible providers remain unknown. Do not log
            // provider exception strings, which could contain application content.
        }

        var after = readForeground();
        if (Volatile.Read(ref stopped) != 0) return;
        var result = new TextInputSnapshot(classification.IsTextInput, classification.IsUnknown,
            foreground.ProcessId, foreground.WindowHandle, Stopwatch.GetTimestamp());
        if (!snapshots.TryPublish(ticket, result, after))
        {
            RequestRefresh();
            return; // Never overwrite a newer focus event or publish a sample for another app.
        }
    }

    private static CacheRequest CreateMetadataCache()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element };
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.IsEnabledProperty);
        request.Add(AutomationElement.HasKeyboardFocusProperty);
        request.Add(AutomationElement.IsKeyboardFocusableProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(AutomationElement.IsValuePatternAvailableProperty);
        request.Add(AutomationElement.IsTextPatternAvailableProperty);
        request.Add(ValuePattern.IsReadOnlyProperty);
        request.Add(TextPattern.Pattern);
        return request;
    }

    private static bool? ReadCachedBool(AutomationElement element, AutomationProperty property) =>
        element.GetCachedPropertyValue(property, true) is bool value ? value : null;

    private static int? ReadCachedInt(AutomationElement element, AutomationProperty property) =>
        element.GetCachedPropertyValue(property, true) is int value ? value : null;

    private static bool? ReadNativeEditableFocus(ForegroundWindowInfo foreground)
    {
        uint threadId = GetWindowThreadProcessId(foreground.WindowHandle, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info) || info.Focus == nint.Zero) return null;
        GetWindowThreadProcessId(info.Focus, out uint focusProcess);
        if (focusProcess != (uint)foreground.ProcessId) return null;
        var className = new StringBuilder(256);
        if (GetClassNameW(info.Focus, className, className.Capacity) == 0) return null;
        string role = className.ToString(); // Window class metadata, never the control's text.
        bool isEdit = role.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || role.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
            || role.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase)
            || role.StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase);
        if (!isEdit) return null;
        long style = nint.Size == 8 ? GetWindowLongPtrW(info.Focus, -16).ToInt64() : GetWindowLongW(info.Focus, -16);
        return (style & 0x0800) == 0; // ES_READONLY; no content or caret queries.
    }

    private static TextInputSnapshot Unknown(ForegroundWindowInfo foreground) =>
        new(false, true, foreground.ProcessId, foreground.WindowHandle, Stopwatch.GetTimestamp());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        try { wake.Set(); } catch (ObjectDisposedException) { }
        // A broken third-party provider can block its COM call indefinitely. This
        // worker is background-only; shutdown never waits indefinitely for UIA.
        if (Thread.CurrentThread != worker) worker.Join(150);
    }

    private sealed record ProcessNameSnapshot(int ProcessId, string? Name);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder className, int maximum);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetWindowLongW(nint window, int index);
}

internal readonly record struct TextInputClassification(bool IsTextInput, bool IsUnknown);

/// <summary>
/// A focus notification and its invalidation are one atomic state replacement.
/// Comparing the captured state object also protects same-window focus changes:
/// a late UIA response cannot replace the newer unknown state with a stale result.
/// </summary>
internal sealed class FocusSnapshotCache
{
    internal sealed record ProbeTicket(TextInputSnapshot Snapshot);
    private ProbeTicket state;

    public FocusSnapshotCache(ForegroundWindowInfo foreground)
    {
        state = MakeUnknown(foreground);
    }

    public TextInputSnapshot Current => Volatile.Read(ref state).Snapshot;
    public ProbeTicket Capture() => Volatile.Read(ref state);

    public void Invalidate(ForegroundWindowInfo foreground) => Volatile.Write(ref state, MakeUnknown(foreground));

    public bool TryPublish(ProbeTicket expected, TextInputSnapshot result, ForegroundWindowInfo foregroundAfter)
    {
        if (result.WindowHandle != foregroundAfter.WindowHandle || result.ProcessId != foregroundAfter.ProcessId)
            return false;
        var replacement = new ProbeTicket(result);
        return ReferenceEquals(Interlocked.CompareExchange(ref state, replacement, expected), expected);
    }

    private static ProbeTicket MakeUnknown(ForegroundWindowInfo foreground) => new(
        new TextInputSnapshot(false, true, foreground.ProcessId, foreground.WindowHandle, Stopwatch.GetTimestamp()));
}

internal readonly record struct FocusedInputMetadata(
    int ControlTypeId,
    bool? IsEnabled,
    bool? HasKeyboardFocus,
    bool? IsKeyboardFocusable,
    bool IsPassword,
    bool HasValuePattern,
    bool? ValueIsReadOnly,
    bool HasTextPattern,
    bool? TextIsReadOnly);

internal static class TextInputClassificationPolicy
{
    public static TextInputClassification Classify(FocusedInputMetadata metadata)
    {
        if (metadata.IsEnabled == false) return new(false, false);
        if (metadata.HasKeyboardFocus == false) return new(false, true);
        if (metadata.IsPassword) return new(true, false);
        if (metadata.HasValuePattern && metadata.ValueIsReadOnly == false) return new(true, false);
        if (metadata.HasTextPattern && metadata.TextIsReadOnly == false) return new(true, false);
        if (metadata.ControlTypeId == ControlType.Edit.Id)
        {
            if (metadata.ValueIsReadOnly == true || metadata.TextIsReadOnly == true) return new(false, false);
            // The focused Edit role itself is useful when a provider omits patterns.
            return new(true, false);
        }
        // A TextPattern by itself may describe a read-only web page, transcript or
        // game label. Generic focused game surfaces therefore do not lock the mic.
        if (metadata.ControlTypeId != 0) return new(false, false);
        return new(false, true);
    }
}
