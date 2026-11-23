using System.Diagnostics;
using Microsoft.Win32;
using MicPtt.Audio;
using MicPtt.Input;

namespace MicPtt.UI;

internal sealed class MainForm : Form
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly TalkStateMachine policy = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 15 };
    private readonly Stopwatch verifyClock = Stopwatch.StartNew();
    private readonly ComboBox devices = new();
    private readonly RoundedButton refresh = new() { Text = "새로고침" };
    private readonly RoundedButton hold = new() { Text = "누르는 동안 말하기" };
    private readonly RoundedButton toggle = new() { Text = "한 번 눌러 전환" };
    private readonly RoundedButton shortcut = new();
    private readonly RoundedButton start = new() { Text = "마이크 제어 시작", Accent = true };
    private readonly RoundedButton hide = new() { Text = "트레이로 숨기기" };
    private readonly RoundedButton preferencesButton = new() { Text = "효과음 · 오버레이 설정" };
    private readonly FeedbackSoundPlayer sounds = new();
    private readonly MicrophoneOverlay overlay = new();
    private readonly ToolStripMenuItem trayOverlay = new("상태 오버레이 표시");
    private PreferencesForm? preferences;
    private bool preferencesOpen;
    private readonly Label statusTitle = new();
    private readonly Label statusDetail = new();
    private readonly Label statusTag = new();
    private readonly Label saveNote = new();
    private readonly NotifyIcon tray = new();
    private readonly ToolStripMenuItem trayStart = new("마이크 제어 시작");
    private readonly ToolStripMenuItem trayMute = new("지금 음소거");
    private readonly Icon idleIcon = Theme.CreateIcon(Theme.Secondary);
    private readonly Icon mutedIcon = Theme.CreateIcon(Theme.Amber);
    private readonly Icon talkingIcon = Theme.CreateIcon(Theme.Mint);
    private readonly ToolTip tips = new();
    private CoreAudio? endpoint;
    private GlobalShortcutObserver? keyboard;
    private bool originalMuted;
    private bool active;
    private bool capturing;
    private bool locked;
    private bool suspended;
    private bool closing;
    private bool? lastApplied;
    private string? notice;

    public MainForm()
    {
        Text = "MicKey · 마이크 단축키";
        ClientSize = new Size(560, 742);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10f);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Icon = idleIcon;
        BuildUi();
        BuildTray();
        overlay.PositionChanged += point => { settings.Overlay.X = point.X; settings.Overlay.Y = point.Y; SaveSettings(); };
        overlay.ApplySettings(settings.Overlay);
        sounds.PlaybackFailed += OnSoundFailure;
        RefreshDevices();
        UpdateUi();
        poll.Tick += (_, _) => Poll();
        poll.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Deactivate += (_, _) => { if (capturing) EndCapture(null); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
    }

    private Label AddLabel(Control parent, string text, Rectangle bounds, float size = 10, Color? color = null, bool bold = false)
    {
        var label = new Label
        {
            Text = text, Bounds = bounds, ForeColor = color ?? Theme.Secondary,
            BackColor = parent.BackColor, Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular),
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
        };
        parent.Controls.Add(label);
        return label;
    }

    private void BuildUi()
    {
        AddLabel(this, "MIC KEY", new(24, 16, 300, 24), 10, Theme.Mint, true);
        AddLabel(this, "말할 때만, 마이크 ON", new(22, 44, 516, 44), 22, Theme.Text, true);

        var card = new Panel { Bounds = new(24, 106, 512, 138), BackColor = Theme.Surface };
        Controls.Add(card);
        statusTag.Bounds = new(20, 12, 472, 24);
        statusTag.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        statusTitle.Bounds = new(20, 40, 472, 42);
        statusTitle.Font = new Font("맑은 고딕", 20, FontStyle.Bold);
        statusDetail.Bounds = new(20, 86, 472, 42);
        statusDetail.ForeColor = Theme.Secondary;
        statusDetail.Font = new Font("맑은 고딕", 9);
        card.Controls.AddRange([statusTag, statusTitle, statusDetail]);

        AddLabel(this, "입력 마이크", new(24, 260, 400, 26), 10, Theme.Text, true);
        devices.Bounds = new(24, 292, 396, 34);
        devices.DropDownStyle = ComboBoxStyle.DropDownList;
        devices.FlatStyle = FlatStyle.Flat;
        devices.BackColor = Theme.Surface;
        devices.ForeColor = Theme.Text;
        devices.Font = new Font("맑은 고딕", 11);
        devices.DisplayMember = nameof(AudioDevice.Name);
        devices.AccessibleName = "입력 마이크 선택";
        devices.SelectedIndexChanged += (_, _) =>
        {
            if (devices.SelectedItem is AudioDevice selected)
            {
                settings.DeviceId = selected.Id;
                tips.SetToolTip(devices, selected.Name);
                SaveSettings();
                UpdateUi();
            }
        };
        Controls.Add(devices);
        refresh.Bounds = new(430, 288, 106, 36);
        refresh.Click += (_, _) => RefreshDevices();
        Controls.Add(refresh);

        AddLabel(this, "말하기 방식", new(24, 343, 400, 26), 10, Theme.Text, true);
        hold.Bounds = new(24, 375, 251, 48);
        toggle.Bounds = new(285, 375, 251, 48);
        hold.Click += (_, _) => SetMode(TalkMode.HoldToTalk);
        toggle.Click += (_, _) => SetMode(TalkMode.ToggleMute);
        tips.SetToolTip(hold, "단축키를 누르는 동안만 마이크를 켭니다. 놓으면 음소거됩니다.");
        tips.SetToolTip(toggle, "단축키를 한 번 누를 때마다 음소거와 켜짐을 전환합니다.");
        Controls.AddRange([hold, toggle]);

        AddLabel(this, "단축키", new(24, 440, 160, 26), 10, Theme.Text, true);
        AddLabel(this, "클릭한 뒤 새 키를 누르세요", new(200, 440, 336, 26), 9);
        shortcut.Bounds = new(24, 474, 512, 46);
        shortcut.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        shortcut.AccessibleName = "단축키 변경";
        shortcut.Click += (_, _) => BeginCapture();
        Controls.Add(shortcut);

        start.Bounds = new(24, 543, 324, 48);
        start.Font = new Font("맑은 고딕", 11, FontStyle.Bold);
        start.Click += (_, _) => { if (active) StopControl(); else StartControl(); };
        hide.Bounds = new(358, 543, 178, 48);
        hide.Click += (_, _) => HideToTray();
        Controls.AddRange([start, hide]);
        preferencesButton.Bounds = new(24, 605, 512, 42);
        preferencesButton.Click += (_, _) => OpenPreferences();
        Controls.Add(preferencesButton);
        AddLabel(this, "로블록스에서는 마이크를 켜 두세요. 다른 앱의 같은 마이크도 함께 제어됩니다.",
            new(24, 662, 512, 38), 8.5f);
        AddLabel(this, "중지·종료하면 시작 전 마이크 상태로 돌아갑니다.", new(24, 699, 512, 22), 8.5f);
        saveNote.Bounds = new(24, 722, 512, 17);
        saveNote.Font = new Font("맑은 고딕", 8);
        saveNote.ForeColor = Theme.Amber;
        Controls.Add(saveNote);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("MicKey 열기", null, (_, _) => RestoreWindow());
        menu.Items.Add(trayStart);
        trayStart.Click += (_, _) => { if (active) StopControl(); else StartControl(); };
        menu.Items.Add(trayMute);
        trayMute.Click += (_, _) => ForceMute();
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("효과음 · 오버레이 설정", null, (_, _) => OpenPreferences());
        menu.Items.Add(trayOverlay);
        trayOverlay.Click += (_, _) => { settings.Overlay.Enabled = !settings.Overlay.Enabled; ApplyPresentationSettings(); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료 · 이전 마이크 상태 복원", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => RestoreWindow();
        tray.Icon = idleIcon;
        tray.Text = "MicKey · 시작 전";
        tray.Visible = true;
    }

    private void RefreshDevices()
    {
        if (active) return;
        try
        {
            var available = CoreAudio.ListCaptureDevices();
            string? wanted = settings.DeviceId ?? CoreAudio.GetDefaultCaptureId();
            var selected = available.FirstOrDefault(d => d.Id == wanted);
            if (selected is null)
            {
                var defaultId = CoreAudio.GetDefaultCaptureId();
                selected = available.FirstOrDefault(d => d.Id == defaultId) ?? available.FirstOrDefault();
            }
            devices.Items.Clear();
            foreach (var device in available) devices.Items.Add(device);
            if (selected is not null) devices.SelectedItem = selected;
            notice = available.Count == 0 ? "연결된 마이크가 없어요. 연결 후 새로고침을 눌러 주세요." : null;
        }
        catch (Exception ex)
        {
            notice = "마이크 목록을 읽지 못했어요. 새로고침으로 다시 시도하세요.";
            Log(ex);
        }
        UpdateUi();
    }

    private void StartControl()
    {
        if (active || devices.SelectedItem is not AudioDevice selected) return;
        if (capturing) EndCapture(null);
        CoreAudio? candidate = null;
        GlobalShortcutObserver? candidateKeyboard = null;
        bool? previous = null;
        try
        {
            candidateKeyboard = new GlobalShortcutObserver(settings.Shortcut);
            candidate = new CoreAudio(selected.Id);
            previous = candidate.IsMuted;
            candidate.SetMuted(true);
            if (!candidate.IsMuted) throw new InvalidOperationException("마이크가 음소거 상태로 바뀌지 않았습니다.");
            originalMuted = previous.Value;
            endpoint = candidate;
            candidate = null;
            keyboard = candidateKeyboard;
            candidateKeyboard = null;
            active = true;
            lastApplied = true;
            notice = null;
            policy.Reset(settings.Mode, settings.Shortcut.IsDown());
            verifyClock.Restart();
            PlayFeedback(true);
        }
        catch (Exception ex)
        {
            if (candidate is not null && previous.HasValue)
            {
                try { candidate.SetMuted(previous.Value); } catch (Exception restoreError) { Log(restoreError); }
            }
            candidate?.Dispose();
            candidateKeyboard?.Dispose();
            notice = "마이크에 연결하지 못했어요. 장치와 Windows 마이크 권한을 확인하세요.";
            Log(ex);
            ShowProblem("마이크 제어를 시작할 수 없습니다.", ex);
        }
        UpdateUi();
    }

    private void StopControl(bool showErrors = true)
    {
        if (!active && endpoint is null) return;
        active = false;
        capturing = false;
        keyboard?.Dispose();
        keyboard = null;
        notice = null;
        try
        {
            if (endpoint is not null)
            {
                endpoint.SetMuted(originalMuted);
                if (endpoint.IsMuted != originalMuted) throw new InvalidOperationException("원래 음소거 상태를 확인할 수 없습니다.");
                if (!closing) PlayFeedback(originalMuted);
            }
        }
        catch (Exception ex)
        {
            notice = "원래 상태를 복원하지 못했어요. Windows에서 마이크 상태를 확인하세요.";
            Log(ex);
            if (showErrors) ShowProblem(notice, ex);
        }
        finally
        {
            endpoint?.Dispose();
            endpoint = null;
            lastApplied = null;
        }
        if (!closing) UpdateUi();
    }

    private void SetMode(TalkMode mode)
    {
        if (capturing) EndCapture(null);
        settings.Mode = mode;
        policy.Reset(mode, settings.Shortcut.IsDown());
        if (active) ForceMute();
        SaveSettings();
        UpdateUi();
    }

    private void BeginCapture()
    {
        if (capturing) { EndCapture(null); return; }
        ForceMute();
        capturing = true;
        shortcut.Focus();
        UpdateUi();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!capturing)
        {
            // Inside our own window, Space/arrows used as a shortcut must not also click
            // the focused settings button. Other applications still receive their input.
            if (active && Hotkey.TryFromKeyData(keyData, out var pressed) && pressed == settings.Shortcut)
                return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        if ((keyData & Keys.KeyCode) == Keys.Escape) EndCapture(null);
        else if (Hotkey.TryFromKeyData(keyData, out var replacement)) EndCapture(replacement);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!capturing && active && Hotkey.TryFromKeyData(e.KeyData, out var pressed) && pressed == settings.Shortcut)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    private void EndCapture(Hotkey? replacement)
    {
        capturing = false;
        if (replacement is not null)
        {
            settings.Shortcut = replacement;
            SaveSettings();
        }
        keyboard?.Configure(settings.Shortcut);
        policy.Reset(settings.Mode, settings.Shortcut.IsDown());
        UpdateUi();
    }

    private void Poll()
    {
        if (!active || endpoint is null) return;
        try
        {
            bool sawTransition = false;
            while (keyboard is not null && keyboard.TryRead(out bool down))
            {
                sawTransition = true;
                if (!capturing && !locked && !suspended && !preferencesOpen)
                {
                    bool eventMuted = policy.Update(down);
                    if (lastApplied != eventMuted) ApplyMuted(eventMuted);
                }
            }
            // Hook events retain even very short taps. Polling only supplies a missing release;
            // it must never invent an extra press and toggle the microphone a second time.
            if (!sawTransition && !capturing && !locked && !suspended && !preferencesOpen && !settings.Shortcut.IsDown())
                policy.Update(false);
            bool desired = capturing || locked || suspended || preferencesOpen ? true : policy.Muted;
            if (lastApplied != desired)
            {
                ApplyMuted(desired);
                UpdateUi();
            }
            else if (sawTransition) UpdateUi();
            if (verifyClock.ElapsedMilliseconds >= 200)
            {
                verifyClock.Restart();
                bool actual = endpoint.IsMuted;
                if (actual != desired)
                {
                    // Respect a mute made outside this app; do not fight a headset's mute button.
                    if (actual) policy.ForceMute(settings.Shortcut.IsDown());
                    ApplyMuted(true);
                    UpdateUi();
                }
            }
        }
        catch (Exception ex) { FailClosed(ex); }
    }

    private void ApplyMuted(bool muted)
    {
        if (endpoint is null) return;
        endpoint.SetMuted(muted);
        if (endpoint.IsMuted != muted) throw new InvalidOperationException("마이크 음소거 상태를 확인하지 못했습니다.");
        bool changed = lastApplied != muted;
        lastApplied = muted;
        verifyClock.Restart();
        if (changed && !locked && !suspended) PlayFeedback(muted);
    }

    private void ForceMute()
    {
        keyboard?.Configure(settings.Shortcut);
        policy.ForceMute(settings.Shortcut.IsDown());
        if (active)
        {
            try { ApplyMuted(true); }
            catch (Exception ex) { FailClosed(ex); }
        }
        UpdateUi();
    }

    private void FailClosed(Exception ex)
    {
        // An inaccessible/disconnected microphone must never cause another endpoint to be opened.
        try { endpoint?.SetMuted(true); } catch (Exception muteError) { Log(muteError); }
        endpoint?.Dispose();
        endpoint = null;
        keyboard?.Dispose();
        keyboard = null;
        active = false;
        lastApplied = null;
        capturing = false;
        notice = "마이크 연결이 끊겼거나 제어에 실패했어요. 새로고침 후 다시 시작하세요.";
        Log(ex);
        UpdateUi();
        tray.ShowBalloonTip(4000, "MicKey · 마이크 제어 중지", notice, ToolTipIcon.Warning);
    }

    private void UpdateUi()
    {
        bool speaking = active && lastApplied == false;
        Color stateColor = speaking ? Theme.Mint : active ? Theme.Amber : Theme.Secondary;
        statusTag.Text = active ? (settings.Mode == TalkMode.HoldToTalk ? "●  PUSH TO TALK" : "●  TOGGLE MUTE") : "○  READY";
        statusTag.ForeColor = stateColor;
        statusTitle.Text = notice is not null ? "마이크를 확인해 주세요" : !active ? "시작할 준비가 됐어요" :
            speaking ? (settings.Mode == TalkMode.HoldToTalk ? "말하는 중" : "마이크 켜짐") : "음소거 중";
        statusTitle.ForeColor = notice is not null ? Theme.Red : stateColor;
        statusDetail.Text = notice ?? (preferencesOpen && active ? "효과음과 오버레이를 설정하는 동안에는 음소거됩니다." :
            capturing ? "새 단축키를 누르세요. Ctrl / Alt / Shift 조합도 가능해요.\nEsc를 누르면 취소합니다." :
            locked || suspended ? "잠금 또는 절전 중에는 마이크를 음소거합니다." :
            !active ? "마이크와 단축키를 선택하고 시작하세요.\n시작할 때는 항상 음소거 상태예요." :
            settings.Mode == TalkMode.HoldToTalk ? $"[ {settings.Shortcut.DisplayName} ] 키를 누르는 동안만 소리가 전달돼요.\n키를 놓으면 다시 음소거됩니다." :
            $"[ {settings.Shortcut.DisplayName} ] 키를 한 번 누르면 상태가 바뀌어요.\n다시 누르면 마이크가 { (speaking ? "꺼집니다" : "켜집니다") }.");
        shortcut.Text = capturing ? "새 단축키 입력…   (Esc 취소)" : settings.Shortcut.DisplayName + "     ·     변경";
        hold.Selected = settings.Mode == TalkMode.HoldToTalk;
        toggle.Selected = settings.Mode == TalkMode.ToggleMute;
        hold.Invalidate(); toggle.Invalidate();
        devices.Enabled = !active;
        refresh.Enabled = !active;
        start.Enabled = active || devices.SelectedItem is AudioDevice;
        start.Text = active ? "제어 중지 · 이전 상태 복원" : "마이크 제어 시작";
        start.Accent = !active;
        start.Invalidate();
        trayStart.Text = start.Text;
        trayStart.Enabled = start.Enabled;
        trayMute.Enabled = active;
        var currentIcon = speaking ? talkingIcon : active ? mutedIcon : idleIcon;
        tray.Icon = currentIcon;
        Icon = currentIcon;
        tray.Text = "MicKey · " + (active ? speaking ? "마이크 켜짐" : "음소거" : "제어 중지") + " · " + settings.Shortcut.DisplayName;
        trayOverlay.Checked = settings.Overlay.Enabled;
        overlay.SetStatus(active, !speaking, settings.Shortcut.DisplayName, notice is not null);
    }

    private void PlayFeedback(bool muted, bool preview = false)
    {
        if (!preview && !settings.Sound.Enabled) return;
        var tone = muted ? settings.Sound.OffTone : settings.Sound.OnTone;
        if (preview && (settings.Sound.VolumePercent == 0 || tone == FeedbackTone.None))
            preferences?.SetSoundMessage(settings.Sound.VolumePercent == 0 ? "볼륨이 0%로 설정되어 있어요." : "이 상태의 효과음은 '소리 없음'으로 설정되어 있어요.");
        try { sounds.Play(!muted, tone, settings.Sound.VolumePercent, settings.Sound.OutputDeviceId); }
        catch (Exception ex) { OnSoundFailure(ex.Message); }
    }

    private void OnSoundFailure(string message)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (closing || IsDisposed) return;
                saveNote.Text = "효과음 출력 장치를 확인하세요. 마이크 제어는 계속 작동합니다.";
                preferences?.SetSoundMessage(message);
            });
        }
        catch (InvalidOperationException) { }
    }

    private void ApplyPresentationSettings()
    {
        settings.Sound.Normalize();
        settings.Overlay.Normalize();
        overlay.ApplySettings(settings.Overlay);
        SaveSettings();
        UpdateUi();
    }

    private void OpenPreferences()
    {
        if (preferencesOpen) { preferences?.Activate(); return; }
        if (capturing) EndCapture(null);
        preferencesOpen = true;
        ForceMute();
        RestoreWindow();
        try
        {
            using var dialog = new PreferencesForm(settings, ApplyPresentationSettings,
                on => PlayFeedback(!on, preview: true), editing => overlay.SetEditing(editing));
            preferences = dialog;
            dialog.ShowDialog(this);
        }
        finally
        {
            preferences = null;
            preferencesOpen = false;
            overlay.SetEditing(false);
            keyboard?.Configure(settings.Shortcut);
            policy.Reset(settings.Mode, settings.Shortcut.IsDown());
            UpdateUi();
        }
    }

    private void SaveSettings()
    {
        try { settings.Save(); saveNote.Text = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            saveNote.Text = "설정을 저장하지 못했어요. 현재 실행에서는 계속 사용할 수 있습니다.";
            Log(ex);
        }
    }

    private void HideToTray()
    {
        if (capturing) EndCapture(null);
        Hide();
        tray.ShowBalloonTip(2000, "MicKey", "트레이에서 계속 실행됩니다. 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            OnUi(() => { locked = true; ForceMute(); });
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
            OnUi(() => { locked = false; policy.Reset(settings.Mode, settings.Shortcut.IsDown()); ForceMute(); });
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnUi(() => { suspended = true; ForceMute(); });
        else if (e.Mode == PowerModes.Resume)
            OnUi(() => { suspended = false; policy.Reset(settings.Mode, settings.Shortcut.IsDown()); ForceMute(); });
    }

    private void OnUi(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { if (InvokeRequired) Invoke(action); else action(); }
        catch (InvalidOperationException) { }
    }

    public void HandleUnexpectedError(Exception ex)
    {
        if (closing) return;
        FailClosed(ex);
        ShowProblem("오류가 발생해 마이크 제어를 중지했습니다.", ex);
    }

    private void ShowProblem(string message, Exception ex)
    {
        RestoreWindow();
        MessageBox.Show(this, message + "\n\n" + ex.Message, "MicKey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void Log(Exception ex)
    {
        try
        {
            var folder = Path.GetDirectoryName(AppSettings.StoragePath)!;
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errors.log"), $"{DateTimeOffset.Now:u} {ex}\n");
        }
        catch { /* Diagnostics must not prevent microphone cleanup. */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true;
        poll.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        StopControl(showErrors: false);
        tray.Visible = false;
        overlay.Hide();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            poll.Dispose();
            sounds.PlaybackFailed -= OnSoundFailure;
            sounds.Dispose();
            overlay.Dispose();
            keyboard?.Dispose();
            endpoint?.Dispose();
            tray.Dispose();
            tips.Dispose();
            idleIcon.Dispose(); mutedIcon.Dispose(); talkingIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
