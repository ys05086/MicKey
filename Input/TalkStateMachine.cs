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
    private bool _inputBlocked;

    public bool Muted { get; private set; } = true;
    public bool InputBlocked => _inputBlocked;
    /// <summary>The last processed chord state, not the observer's final queued state.</summary>
    public bool LastKeyDown => _wasDown;

    public void Reset(TalkMode mode, bool keyDown)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        _inputBlocked = false;
        ForceMute(keyDown);
    }

    /// <summary>
    /// Pauses shortcut activation without changing an existing toggle-mode state.
    /// Hold mode mutes immediately. On leaving the block, a held shortcut must be
    /// released before it can activate again. Returns the desired mute state.
    /// Reapplying an unchanged, unblocked state does not consume a pending edge.
    /// </summary>
    public bool SetInputBlocked(bool blocked, bool keyDown)
    {
        if (!blocked && !_inputBlocked) return Muted;

        _inputBlocked = blocked;
        _wasDown = keyDown;
        _waitingForRelease = keyDown;
        if (_mode == TalkMode.HoldToTalk) Muted = true;
        return Muted;
    }

    /// <summary>Returns the desired mute state after observing the shortcut.</summary>
    public bool Update(bool keyDown)
    {
        if (_inputBlocked)
        {
            // Consume both edges while blocked. They must never be replayed when
            // focus returns, and a hold-mode release must always leave it muted.
            _wasDown = keyDown;
            _waitingForRelease = keyDown;
            if (_mode == TalkMode.HoldToTalk) Muted = true;
            return Muted;
        }

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
    /// Applies protection at this event's position in an ordered input queue,
    /// then observes the edge. A fresh press after a blocked release is allowed;
    /// an already-held shortcut still requires release before reactivation.
    /// </summary>
    public bool Update(bool keyDown, bool inputBlocked)
    {
        SetInputBlocked(inputBlocked, _wasDown);
        return Update(keyDown);
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
