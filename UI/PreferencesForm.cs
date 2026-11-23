using MicPtt.Audio;

namespace MicPtt.UI;

internal sealed class PreferencesForm : Form
{
    private readonly AppSettings settings;
    private readonly Action changed;
    private readonly Action<bool> preview;
    private readonly Action<bool> editOverlay;
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

    public PreferencesForm(AppSettings settings, Action changed, Action<bool> preview, Action<bool> editOverlay)
    {
        this.settings = settings;
        this.changed = changed;
        this.preview = preview;
        this.editOverlay = editOverlay;
        Text = "MicKey · 효과음과 오버레이";
        ClientSize = new Size(580, 592);
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
        Label(this, "내게 맞는 소리와 상태 표시", new(22, 20, 536, 40), 19, true);
        soundTab.Bounds = new(24, 77, 261, 42);
        overlayTab.Bounds = new(295, 77, 261, 42);
        soundTab.Click += (_, _) => SelectTab(false);
        overlayTab.Click += (_, _) => SelectTab(true);
        Controls.AddRange([soundTab, overlayTab]);
        soundPanel.Bounds = overlayPanel.Bounds = new(24, 139, 532, 370);
        soundPanel.BackColor = overlayPanel.BackColor = Theme.Background;
        Controls.AddRange([soundPanel, overlayPanel]);
        BuildSounds();
        BuildOverlay();
        overlayPanel.Visible = false;
        Label(this, "자동 저장됩니다. 제어 중 설정을 열면 마이크를 음소거합니다.", new(24, 516, 532, 24), 9, false, Theme.Secondary);
        var close = new RoundedButton { Text = "닫기", Accent = true, Bounds = new(416, 550, 140, 34) };
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

    private void SelectTab(bool overlay)
    {
        soundPanel.Visible = !overlay;
        overlayPanel.Visible = overlay;
        soundTab.Selected = !overlay;
        overlayTab.Selected = overlay;
        soundTab.Invalidate(); overlayTab.Invalidate();
        if (!overlay && editing)
        {
            editing = false;
            editOverlay(false);
            positionButton.Text = "위치 미리보기 · 이동";
            positionButton.Selected = false;
        }
    }

    public void SetSoundMessage(string message) => soundMessage.Text = message;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        editOverlay(false);
        base.OnFormClosed(e);
    }

    private sealed record ToneChoice(FeedbackTone Tone, string Name);
}
