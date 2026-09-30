using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisDevUnlock;

/// <summary>
/// Keyboard reads via the Input System package.
///
/// This build sets Active Input Handling to "Input System Package (New)", which breaks the
/// two obvious alternatives: UnityEngine.Input throws on any read, and IMGUI's Event.current
/// never receives keyboard events because those are fed by the disabled legacy backend.
/// So the console renders with IMGUI but reads its keys here.
/// </summary>
internal static class KeyboardInput
{
    private const float RepeatDelay = 0.4f;
    private const float RepeatInterval = 0.04f;

    private static readonly (Key Key, char Plain, char Shifted)[] Printable =
    {
        (Key.A, 'a', 'A'), (Key.B, 'b', 'B'), (Key.C, 'c', 'C'), (Key.D, 'd', 'D'),
        (Key.E, 'e', 'E'), (Key.F, 'f', 'F'), (Key.G, 'g', 'G'), (Key.H, 'h', 'H'),
        (Key.I, 'i', 'I'), (Key.J, 'j', 'J'), (Key.K, 'k', 'K'), (Key.L, 'l', 'L'),
        (Key.M, 'm', 'M'), (Key.N, 'n', 'N'), (Key.O, 'o', 'O'), (Key.P, 'p', 'P'),
        (Key.Q, 'q', 'Q'), (Key.R, 'r', 'R'), (Key.S, 's', 'S'), (Key.T, 't', 'T'),
        (Key.U, 'u', 'U'), (Key.V, 'v', 'V'), (Key.W, 'w', 'W'), (Key.X, 'x', 'X'),
        (Key.Y, 'y', 'Y'), (Key.Z, 'z', 'Z'),
        (Key.Digit1, '1', '!'), (Key.Digit2, '2', '@'), (Key.Digit3, '3', '#'),
        (Key.Digit4, '4', '$'), (Key.Digit5, '5', '%'), (Key.Digit6, '6', '^'),
        (Key.Digit7, '7', '&'), (Key.Digit8, '8', '*'), (Key.Digit9, '9', '('),
        (Key.Digit0, '0', ')'),
        (Key.Space, ' ', ' '), (Key.Minus, '-', '_'), (Key.Equals, '=', '+'),
        (Key.Period, '.', '>'), (Key.Comma, ',', '<'), (Key.Slash, '/', '?'),
        (Key.Semicolon, ';', ':'), (Key.Quote, '\'', '"'), (Key.Backslash, '\\', '|'),
        (Key.LeftBracket, '[', '{'), (Key.RightBracket, ']', '}'), (Key.Backquote, '`', '~'),
    };

    private static Key _repeatingKey = Key.None;
    private static float _repeatTimer;

    public static bool Available => Keyboard.current != null;

    public static bool WasPressed(Key key)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || key == Key.None)
            return false;

        var control = keyboard[key];
        return control != null && control.wasPressedThisFrame;
    }

    public static bool IsHeld(Key key)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null || key == Key.None)
            return false;

        var control = keyboard[key];
        return control != null && control.isPressed;
    }

    private static bool ShiftHeld => IsHeld(Key.LeftShift) || IsHeld(Key.RightShift);

    /// <summary>Characters typed this frame, honouring shift.</summary>
    public static string TypedThisFrame()
    {
        if (Keyboard.current == null)
            return "";

        var shift = ShiftHeld;
        var typed = "";

        foreach (var (key, plain, shifted) in Printable)
        {
            if (WasPressed(key))
                typed += shift ? shifted : plain;
        }

        return typed;
    }

    /// <summary>
    /// True on the initial press and then again on a repeat tick, so holding backspace
    /// deletes continuously. Only one key repeats at a time, which is all we need.
    /// </summary>
    public static bool PressedOrRepeating(Key key)
    {
        if (WasPressed(key))
        {
            _repeatingKey = key;
            _repeatTimer = RepeatDelay;
            return true;
        }

        if (_repeatingKey != key || !IsHeld(key))
        {
            if (_repeatingKey == key)
                _repeatingKey = Key.None;
            return false;
        }

        // Console pauses the game, so scaled delta time would be zero.
        _repeatTimer -= Time.unscaledDeltaTime;
        if (_repeatTimer > 0f)
            return false;

        _repeatTimer = RepeatInterval;
        return true;
    }
}
