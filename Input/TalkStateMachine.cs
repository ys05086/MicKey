namespace MicPtt.Input;

public enum TalkMode
{
    HoldToTalk,
    ToggleMute
}

/// <summary>
/// Pure input policy. The owner applies Muted to the audio device and must call
/// ForceMute/Reset on lock, suspend, device changes, or other safety interruptions.
/// </summary>
public sealed class TalkStateMachine
{
    private TalkMode _mode = TalkMode.HoldToTalk;
    private bool _wasDown;
    private bool _waitingForRelease;

    public bool Muted { get; private set; } = true;

    public void Reset(TalkMode mode, bool keyDown)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        ForceMute(keyDown);
    }

    /// <summary>Returns the desired mute state after observing the shortcut.</summary>
    public bool Update(bool keyDown)
    {
        if (_waitingForRelease)
        {
            if (!keyDown) _waitingForRelease = false;
            _wasDown = keyDown;
            return Muted;
        }

        if (_mode == TalkMode.HoldToTalk)
        {
            Muted = !keyDown;
        }
        else if (keyDown && !_wasDown)
        {
            Muted = !Muted;
        }

        _wasDown = keyDown;
        return Muted;
    }

    /// <summary>
    /// Mutes immediately. A shortcut already being held cannot open the microphone
    /// again until it has been released and pressed anew.
    /// </summary>
    public void ForceMute(bool keyDown)
    {
        Muted = true;
        _wasDown = keyDown;
        _waitingForRelease = keyDown;
    }
}
