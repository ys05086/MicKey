using MicPtt.Audio;
using MicPtt.Input;
using System.Diagnostics;

namespace MicPtt.UI;

internal sealed class PreferencesForm : Form
{
    private readonly AppSettings settings;
    private readonly Action changed;
    private readonly Action<bool> preview;
    private readonly Action<bool> editOverlay;
    private readonly Action saveNow;
    private readonly Action<string> exportSettings;
    private readonly Action<string> importSettings;
    private readonly Panel inputPanel = new();
    private readonly RoundedButton inputTab = new() { Text = "입력 · 저장" };
    private readonly Label settingsMessage = new();
    private Action<Hotkey>? captureTarget;
    private Action? cancelCapture;
    private readonly Panel soundPanel = new();
    private readonly Panel overlayPanel = new();
    private readonly RoundedButton soundTab = new() { Text = "효과음", Selected = true };
    private readonly RoundedButton overlayTab = new() { Text = "오버레이" };
    private readonly ComboBox output = new();
    private readonly Label soundMessage = new();
    private readonly RoundedButton positionButton = new() { Text = "위치 미리보기 · 이동" };
    private readonly CheckBox enabledOverlay = new() { Text = "상태 오버레이 표시" };
    private bool editing;
    private bool loadingOutputs;

    public PreferencesForm(AppSettings settings, Action changed, Action<bool> preview, Action<bool> editOverlay,
        Action saveNow, Action<string> exportSettings, Action<string> importSettings)
    {
        this.settings = settings;
        this.changed = changed;
        this.preview = preview;
        this.editOverlay = editOverlay;
        this.saveNow = saveNow;
        this.exportSettings = exportSettings;
        this.importSettings = importSettings;
        Text = "MicKey · 설정";
        ClientSize = new Size(580, 720);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("맑은 고딕", 10);
        ForeColor = Theme.Text;
        BackColor = Theme.Background;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        Label(this, "내게 맞는 마이크 설정", new(22, 20, 536, 40), 19, true);
        soundTab.Bounds = new(24, 77, 171, 42);
        overlayTab.Bounds = new(204, 77, 172, 42);
        inputTab.Bounds = new(385, 77, 171, 42);
        soundTab.Click += (_, _) => SelectTab(0);
        overlayTab.Click += (_, _) => SelectTab(1);
        inputTab.Click += (_, _) => SelectTab(2);
        Controls.AddRange([soundTab, overlayTab, inputTab]);
        soundPanel.Bounds = overlayPanel.Bounds = inputPanel.Bounds = new(24, 139, 532, 496);
        soundPanel.BackColor = overlayPanel.BackColor = inputPanel.BackColor = Theme.Background;
        Controls.AddRange([soundPanel, overlayPanel, inputPanel]);
        BuildSounds();
        BuildOverlay();
        BuildInput();
        overlayPanel.Visible = false;
        inputPanel.Visible = false;
        Label(this, "자동 저장됩니다. 제어 중 설정을 열면 마이크를 음소거합니다.", new(24, 644, 532, 24), 9, false, Theme.Secondary);
        var close = new RoundedButton { Text = "닫기", Accent = true, Bounds = new(416, 678, 140, 34) };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        CancelButton = close;
    }

    private static Label Label(Control parent, string text, Rectangle bounds, float size = 10, bool bold = false, Color? color = null)
    {
        var label = new Label { Text = text, Bounds = bounds, ForeColor = color ?? Theme.Text, BackColor = parent.BackColor,
            Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular), AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft };
        parent.Controls.Add(label);
        return label;
    }

    private static void StyleCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Theme.Surface;
        combo.ForeColor = Theme.Text;
        combo.IntegralHeight = false;
        combo.DropDownHeight = 240;
    }

    private void BuildSounds()
    {
        var enabled = new CheckBox { Text = "마이크 상태가 바뀔 때 효과음 재생", Bounds = new(0, 0, 532, 28), Checked = settings.Sound.Enabled };
        soundPanel.Controls.Add(enabled);
        enabled.CheckedChanged += (_, _) => { settings.Sound.Enabled = enabled.Checked; changed(); };

        Label(soundPanel, "이어폰 / 출력 장치", new(0, 37, 532, 25), 10, true);
        output.Bounds = new(0, 69, 418, 32);
        output.AccessibleName = "효과음 출력 장치";
        output.DisplayMember = nameof(SoundOutputDevice.Name);
        StyleCombo(output);
        output.SelectedIndexChanged += (_, _) =>
        {
            if (loadingOutputs || output.SelectedItem is not SoundOutputDevice selected) return;
            settings.Sound.OutputDeviceId = selected.Id;
            soundMessage.Text = "";
            changed();
        };
        soundPanel.Controls.Add(output);
        var refresh = new RoundedButton { Text = "새로고침", Bounds = new(428, 65, 104, 34) };
        refresh.Click += (_, _) => LoadOutputs();
        soundPanel.Controls.Add(refresh);
        Label(soundPanel, "이어폰을 직접 선택하면 그 장치로만 재생돼요.", new(0, 102, 532, 24), 9, false, Theme.Secondary);
        AddSlider(soundPanel, "효과음 볼륨", 136, 0, 100, settings.Sound.VolumePercent,
            value => { settings.Sound.VolumePercent = value; changed(); });
        AddToneRow("켤 때 / 누를 때", 232, settings.Sound.OnTone, true);
        AddToneRow("끌 때 / 놓을 때", 282, settings.Sound.OffTone, false);
        soundMessage.Bounds = new(0, 330, 532, 40);
        soundMessage.ForeColor = Theme.Amber;
        soundMessage.Font = new Font("맑은 고딕", 9);
        soundPanel.Controls.Add(soundMessage);
        LoadOutputs();
    }

    private void AddToneRow(string title, int y, FeedbackTone selected, bool microphoneOn)
    {
        Label(soundPanel, title, new(0, y, 160, 34), 10, true);
        var tone = new ComboBox { Bounds = new(164, y + 3, 244, 32), DisplayMember = nameof(ToneChoice.Name), AccessibleName = title + " 효과음" };
        StyleCombo(tone);
        foreach (var choice in new[] { new ToneChoice(FeedbackTone.Soft, "부드러운 알림음"), new ToneChoice(FeedbackTone.Digital, "디지털 비프"),
            new ToneChoice(FeedbackTone.Click, "짧은 클릭"), new ToneChoice(FeedbackTone.None, "소리 없음") }) tone.Items.Add(choice);
        tone.SelectedItem = tone.Items.Cast<ToneChoice>().First(t => t.Tone == selected);
        tone.SelectedIndexChanged += (_, _) =>
        {
            if (tone.SelectedItem is not ToneChoice choice) return;
            if (microphoneOn) settings.Sound.OnTone = choice.Tone; else settings.Sound.OffTone = choice.Tone;
            changed();
        };
        soundPanel.Controls.Add(tone);
        var test = new RoundedButton { Text = "미리 듣기", Bounds = new(418, y, 114, 36), AccessibleName = title + " 미리 듣기" };
        test.Click += (_, _) => { soundMessage.Text = ""; preview(microphoneOn); };
        soundPanel.Controls.Add(test);
    }

    private void BuildOverlay()
    {
        enabledOverlay.Bounds = new(0, 0, 532, 28);
        enabledOverlay.Checked = settings.Overlay.Enabled;
        overlayPanel.Controls.Add(enabledOverlay);
        enabledOverlay.CheckedChanged += (_, _) => { settings.Overlay.Enabled = enabledOverlay.Checked; changed(); };
        var inactive = new CheckBox { Text = "마이크 제어가 꺼져 있어도 표시", Bounds = new(0, 38, 532, 28), Checked = settings.Overlay.ShowWhenInactive };
        overlayPanel.Controls.Add(inactive);
        inactive.CheckedChanged += (_, _) => { settings.Overlay.ShowWhenInactive = inactive.Checked; changed(); };
        AddSlider(overlayPanel, "크기", 88, 75, 175, settings.Overlay.ScalePercent,
            value => { settings.Overlay.ScalePercent = value; changed(); });
        AddSlider(overlayPanel, "불투명도", 182, 35, 100, settings.Overlay.OpacityPercent,
            value => { settings.Overlay.OpacityPercent = value; changed(); });
        positionButton.Bounds = new(0, 280, 260, 42);
        positionButton.Click += (_, _) =>
        {
            editing = !editing;
            editOverlay(editing);
            positionButton.Text = editing ? "위치 이동 완료" : "위치 미리보기 · 이동";
            positionButton.Selected = editing;
            positionButton.Invalidate();
        };
        overlayPanel.Controls.Add(positionButton);
        var reset = new RoundedButton { Text = "위치 초기화", Bounds = new(272, 280, 260, 42) };
        reset.Click += (_, _) => { settings.Overlay.X = null; settings.Overlay.Y = null; changed(); };
        overlayPanel.Controls.Add(reset);
        Label(overlayPanel, "미리보기 상태에서 표시창을 드래그하세요. 평소에는 클릭을 통과시킵니다.\n게임의 전체 화면에서 안 보이면 창 모드로 사용하세요.",
            new(0, 330, 532, 40), 9, false, Theme.Secondary);
    }

    private void AddSlider(Control parent, string title, int y, int minimum, int maximum, int value, Action<int> apply)
    {
        Label(parent, title, new(0, y, 370, 28), 10, true);
        var number = Label(parent, value + "%", new(438, y, 94, 28), 11, true, Theme.Mint);
        number.TextAlign = ContentAlignment.MiddleRight;
        var slider = new TrackBar { Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = 25,
            SmallChange = 5, LargeChange = 10, Bounds = new(0, y + 31, 532, 46), BackColor = Theme.Background, AccessibleName = title };
        slider.ValueChanged += (_, _) => { number.Text = slider.Value + "%"; apply(slider.Value); };
        parent.Controls.Add(slider);
    }

    private void LoadOutputs()
    {
        loadingOutputs = true;
        try
        {
            var available = FeedbackSoundPlayer.ListOutputDevices();
            output.Items.Clear();
            foreach (var device in available) output.Items.Add(device);
            var selected = available.FirstOrDefault(d => d.Id == settings.Sound.OutputDeviceId);
            if (selected is null)
            {
                selected = new SoundOutputDevice(settings.Sound.OutputDeviceId, "연결되지 않은 출력 장치 · 다시 선택하세요");
                output.Items.Add(selected);
            }
            output.SelectedItem = selected;
        }
        catch (Exception ex) { SetSoundMessage(ex.Message); }
        finally { loadingOutputs = false; }
    }

    private void SelectTab(int tab)
    {
        cancelCapture?.Invoke(); captureTarget = null; cancelCapture = null;
        soundPanel.Visible = tab == 0;
        overlayPanel.Visible = tab == 1;
        inputPanel.Visible = tab == 2;
        soundTab.Selected = tab == 0;
        overlayTab.Selected = tab == 1;
        inputTab.Selected = tab == 2;
        soundTab.Invalidate(); overlayTab.Invalidate(); inputTab.Invalidate();
        if (tab != 1 && editing)
        {
            editing = false;
            editOverlay(false);
            positionButton.Text = "위치 미리보기 · 이동";
            positionButton.Selected = false;
        }
    }

    private void BuildInput()
    {
        var detect = new CheckBox { Text = "입력창에서 마이크 단축키 자동 잠금", Bounds = new(0, 0, 532, 28), Checked = settings.InputGuard.AutoDetectTextInput };
        detect.CheckedChanged += (_, _) => { settings.InputGuard.AutoDetectTextInput = detect.Checked; changed(); };
        inputPanel.Controls.Add(detect);
        Label(inputPanel, "커서 표시가 아닌 입력 포커스를 감지합니다. 토글 모드는 현재 마이크 상태를 유지합니다.",
            new(0, 32, 532, 34), 8.5f, false, Theme.Secondary);
        Label(inputPanel, "수동 단축키 잠금 / 해제", new(0, 74, 214, 28), 9, true);
        var pause = new RoundedButton { Text = settings.InputGuard.PauseShortcut.DisplayName + " · 변경", Bounds = new(220, 70, 312, 36) };
        pause.Click += (_, _) => StartCapture(pause, () => settings.InputGuard.PauseShortcut, key =>
        {
            if (key == settings.Shortcut) { settingsMessage.Text = "마이크 단축키와 다른 키를 골라 주세요."; return; }
            settings.InputGuard.PauseShortcut = key; changed();
        });
        inputPanel.Controls.Add(pause);
        var game = new CheckBox { Text = "게임/앱의 채팅 열기 키로 입력 보호", Bounds = new(0, 120, 532, 28), Checked = settings.InputGuard.ChatKeyProtection };
        game.CheckedChanged += (_, _) => { settings.InputGuard.ChatKeyProtection = game.Checked; changed(); };
        inputPanel.Controls.Add(game);
        var rules = new ListBox { Bounds = new(0, 155, 532, 76), BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        inputPanel.Controls.Add(rules);
        Label(inputPanel, "앱 선택 / 실행파일 이름", new(0, 232, 260, 18), 8, false, Theme.Secondary);
        var process = new ComboBox { Bounds = new(0, 255, 260, 32), BackColor = Theme.Surface, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat,
            AccessibleName = "채팅 보호 앱 실행파일 이름" };
        var processes = new List<string>();
        foreach (var item in Process.GetProcesses())
        {
            try { if (item.MainWindowHandle != 0 && item.Id != Environment.ProcessId) processes.Add(item.ProcessName); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { item.Dispose(); }
        }
        process.Items.AddRange(processes.Distinct(StringComparer.OrdinalIgnoreCase).Order().Cast<object>().ToArray());
        inputPanel.Controls.Add(process);
        Hotkey openKey = new(Keys.T);
        var chatKey = new RoundedButton { Text = "T · 채팅 키", Bounds = new(270, 251, 142, 36) };
        chatKey.Click += (_, _) => StartCapture(chatKey, () => openKey, key => openKey = key);
        inputPanel.Controls.Add(chatKey);
        void RefreshRules()
        {
            rules.Items.Clear();
            foreach (var rule in settings.InputGuard.ChatRules) rules.Items.Add(rule);
        }
        rules.SelectedIndexChanged += (_, _) =>
        {
            if (rules.SelectedItem is not ChatKeyRule rule) return;
            process.Text = rule.ProcessName;
            openKey = rule.OpenShortcut;
            chatKey.Text = openKey.DisplayName + " · 채팅 키";
        };
        var add = new RoundedButton { Text = "추가/수정", Bounds = new(422, 251, 110, 36) };
        add.Click += (_, _) =>
        {
            var rule = new ChatKeyRule { ProcessName = process.Text, OpenShortcut = openKey };
            rule.Normalize();
            if (rule.ProcessName.Length == 0) { settingsMessage.Text = "목록에서 앱을 고르거나 실행파일 이름을 입력하세요. 예: RobloxPlayerBeta"; return; }
            if (settings.InputGuard.ChatRules.Count >= 32 && !settings.InputGuard.ChatRules.Any(r => r.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase)))
            { settingsMessage.Text = "앱은 최대 32개까지 등록할 수 있어요. 기존 항목을 수정하거나 삭제하세요."; return; }
            settings.InputGuard.ChatRules.RemoveAll(r => r.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase));
            settings.InputGuard.ChatRules.Add(rule);
            changed(); RefreshRules();
            settingsMessage.Text = rule.OpenShortcut == settings.Shortcut
                ? "채팅 키를 저장했어요. 마이크 키와 같으므로 이 앱에서는 채팅 보호가 우선합니다."
                : "채팅 키를 저장했어요. 이 앱에서 " + rule.OpenShortcut.DisplayName + " 키를 누르면 보호합니다.";
        };
        inputPanel.Controls.Add(add);
        var remove = new RoundedButton { Text = "선택 삭제", Bounds = new(0, 296, 110, 34) };
        remove.Click += (_, _) =>
        {
            if (rules.SelectedItem is not ChatKeyRule rule) return;
            settings.InputGuard.ChatRules.Remove(rule); changed(); RefreshRules();
        };
        inputPanel.Controls.Add(remove);
        Label(inputPanel, "Enter/Esc로 채팅 보호 해제 · Shift+Enter는 유지\n마우스로 여는 채팅은 자동 감지 또는 수동 잠금을 사용하세요.",
            new(120, 294, 412, 44), 8.5f, false, Theme.Secondary);
        Label(inputPanel, "설정 파일", new(0, 350, 532, 26), 10, true);
        var save = new RoundedButton { Text = "설정 저장", Bounds = new(0, 383, 170, 38) };
        var export = new RoundedButton { Text = "파일로 내보내기", Bounds = new(180, 383, 172, 38) };
        var import = new RoundedButton { Text = "파일 불러오기", Bounds = new(362, 383, 170, 38) };
        save.Click += (_, _) => RunSettingsAction(() => { saveNow(); settingsMessage.Text = "설정을 저장했어요. 앱을 다시 열어도 유지됩니다."; });
        export.Click += (_, _) => RunSettingsAction(() =>
        {
            using var dialog = new SaveFileDialog { Filter = "MicKey 설정 (*.json)|*.json", FileName = "MicKey-settings.json", DefaultExt = "json", AddExtension = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { exportSettings(dialog.FileName); settingsMessage.Text = "설정 파일을 저장했어요."; }
        });
        import.Click += (_, _) => RunSettingsAction(() =>
        {
            using var dialog = new OpenFileDialog { Filter = "MicKey 설정 (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { importSettings(dialog.FileName); Close(); }
        });
        inputPanel.Controls.AddRange([save, export, import]);
        Label(inputPanel, "불러오면 제어를 중지합니다. 다른 PC에서는 마이크·출력을 다시 선택하세요.", new(0, 426, 532, 28), 8.5f, false, Theme.Secondary);
        settingsMessage.Bounds = new(0, 458, 532, 38);
        settingsMessage.ForeColor = Theme.Mint;
        settingsMessage.Font = new Font("맑은 고딕", 8.5f);
        inputPanel.Controls.Add(settingsMessage);
        RefreshRules();
    }

    private void RunSettingsAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        { settingsMessage.Text = "설정을 처리하지 못했어요: " + ex.Message; }
    }

    private void StartCapture(RoundedButton button, Func<Hotkey> current, Action<Hotkey> apply)
    {
        cancelCapture?.Invoke();
        cancelCapture = () => button.Text = current().DisplayName + " · 변경";
        captureTarget = key => { apply(key); button.Text = current().DisplayName + " · 변경"; };
        button.Text = "새 키 입력… (Esc 취소)";
        button.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (captureTarget is null) return base.ProcessCmdKey(ref msg, keyData);
        if ((keyData & Keys.KeyCode) == Keys.Escape) cancelCapture?.Invoke();
        else if (Hotkey.TryFromKeyData(keyData, out var replacement)) captureTarget(replacement!);
        else return true;
        captureTarget = null; cancelCapture = null;
        return true;
    }

    public void SetSoundMessage(string message) => soundMessage.Text = message;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        editOverlay(false);
        base.OnFormClosed(e);
    }

    private sealed record ToneChoice(FeedbackTone Tone, string Name);
}
