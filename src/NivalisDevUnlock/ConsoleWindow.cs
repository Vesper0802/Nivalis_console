using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisDevUnlock;

/// <summary>
/// Overlay console. IMGUI draws it, KeyboardInput reads it.
///
/// Drawing deliberately uses only GUI.Box and GUI.Label at explicit rects. Neither
/// GUILayout nor GUI.Window is usable here: the generated interop assembly is missing
/// UnityEngine.LayoutedWindow's constructor, so GUILayout.Window throws
/// MissingMethodException on every frame. Avoiding windows also avoids having to marshal
/// a GUI.WindowFunction delegate into IL2CPP.
/// </summary>
public class ConsoleWindow : MonoBehaviour
{
    private const int BaseFontSize = 12;
    private const float Padding = 8f;

    private static readonly List<string> Output = new();
    private static readonly List<string> History = new();

    private bool _open;
    private string _input = "";
    private int _historyIndex = -1;
    private int _scrollBack;
    private float _savedTimeScale = 1f;
    private bool _caretOn = true;
    private float _caretTimer;

    public ConsoleWindow(IntPtr ptr) : base(ptr) { }

    public static string OutputText => string.Join("\n", Output);

    public static void Print(string line)
    {
        foreach (var part in (line ?? "").Split('\n'))
        {
            Output.Add(part);
            // Mirrored so the log is a full transcript, which is the only way to debug
            // what happened in a session after the fact.
            Plugin.Instance?.Log.LogInfo($"[console] {part}");
        }

        if (Output.Count > 400)
            Output.RemoveRange(0, Output.Count - 400);
    }

    private void Start()
    {
        Print("Nivalis console ready. Type 'help' and press Enter.");
        if (!KeyboardInput.Available)
            Print("WARNING: Keyboard.current is null, typing will not work.");
    }

    private void Update()
    {
        if (KeyboardInput.WasPressed(Plugin.ToggleKey.Value))
        {
            Toggle();
            return;
        }

        if (!_open)
            return;

        if (KeyboardInput.WasPressed(Key.Escape))
        {
            Toggle();
            return;
        }

        _caretTimer += Time.unscaledDeltaTime;
        if (_caretTimer >= 0.5f)
        {
            _caretTimer = 0f;
            _caretOn = !_caretOn;
        }

        if (KeyboardInput.CtrlHeld)
        {
            if (KeyboardInput.WasPressed(Key.V))
            {
                Paste();
                return;
            }

            if (KeyboardInput.WasPressed(Key.C))
            {
                GUIUtility.systemCopyBuffer = _input;
                Print($"Copied input line to clipboard.");
                return;
            }
        }

        if (KeyboardInput.WasPressed(Key.Enter) || KeyboardInput.WasPressed(Key.NumpadEnter))
        {
            Submit();
            return;
        }

        if (KeyboardInput.PressedOrRepeating(Key.Backspace))
        {
            if (_input.Length == 0)
                return;

            // Ctrl+Backspace deletes the trailing word, like most shells.
            if (KeyboardInput.CtrlHeld)
            {
                var trimmed = _input.TrimEnd();
                var cut = trimmed.LastIndexOf(' ');
                _input = cut < 0 ? "" : trimmed[..(cut + 1)];
            }
            else
            {
                _input = _input[..^1];
            }
            return;
        }

        if (KeyboardInput.WasPressed(Key.UpArrow))
        {
            StepHistory(1);
            return;
        }

        if (KeyboardInput.WasPressed(Key.DownArrow))
        {
            StepHistory(-1);
            return;
        }

        if (KeyboardInput.PressedOrRepeating(Key.PageUp))
        {
            _scrollBack += 5;
            return;
        }

        if (KeyboardInput.PressedOrRepeating(Key.PageDown))
        {
            _scrollBack = Mathf.Max(0, _scrollBack - 5);
            return;
        }

        var typed = KeyboardInput.TypedThisFrame();
        if (typed.Length > 0)
        {
            _input += typed;
            _scrollBack = 0;
        }
    }

    private void Paste()
    {
        var clip = GUIUtility.systemCopyBuffer;
        if (string.IsNullOrEmpty(clip))
        {
            Print("Clipboard is empty.");
            return;
        }

        // The prompt is a single line, so flatten any structure in the pasted text.
        _input += clip.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
    }

    private void Toggle()
    {
        _open = !_open;

        if (_open)
        {
            if (Plugin.PauseWhileOpen.Value)
            {
                _savedTimeScale = Time.timeScale;
                Time.timeScale = 0f;
            }

            if (Plugin.BlockGameInput.Value)
                GameInput.Suspend();
        }
        else
        {
            if (Plugin.PauseWhileOpen.Value)
                Time.timeScale = _savedTimeScale;

            GameInput.Resume();
        }
    }

    private void OnGUI()
    {
        if (!_open)
            return;

        // Set a real font size rather than scaling a small bitmap through GUI.matrix:
        // scaling made the glyphs blurry and clipped descenders like p and y, because the
        // style's padding ate into a line box sized for the unscaled font.
        var scale = Mathf.Clamp(Plugin.UiScale.Value, 0.5f, 6f);
        var fontSize = Mathf.RoundToInt(BaseFontSize * scale);
        var style = GUI.skin.label;
        style.fontSize = fontSize;
        style.wordWrap = false;

        // Descenders need headroom beyond the nominal font size.
        var lineHeight = fontSize + 8f;

        var margin = 20f;
        var width = Screen.width - margin * 2f;
        var height = Mathf.Min(Screen.height - margin * 2f, lineHeight * 16f);
        var panel = new Rect(margin, margin, width, height);

        GUI.color = Color.white;
        // Stacked because the default box texture is translucent; repeating it darkens the
        // panel enough to read white text over a bright scene.
        for (var i = 0; i < 4; i++)
            GUI.Box(panel, "");

        var x = panel.x + Padding;
        var innerWidth = panel.width - Padding * 2f;
        var y = panel.y + Padding;

        GUI.Label(new Rect(x, y, innerWidth, lineHeight),
            $"Nivalis Console  —  Enter runs, Up/Down history, PageUp/PageDown scroll, " +
            $"Ctrl+V paste, Ctrl+C copy, {Plugin.ToggleKey.Value}/Escape closes");
        y += lineHeight + 4f;

        // Two lines are reserved at the bottom for the prompt and its separator.
        var outputHeight = panel.yMax - Padding - (lineHeight * 2f) - y;
        var visibleLines = Mathf.Max(1, Mathf.FloorToInt(outputHeight / lineHeight));

        _scrollBack = Mathf.Clamp(_scrollBack, 0, Mathf.Max(0, Output.Count - visibleLines));
        var end = Output.Count - _scrollBack;
        var start = Mathf.Max(0, end - visibleLines);

        for (var i = start; i < end; i++)
        {
            GUI.Label(new Rect(x, y, innerWidth, lineHeight), Output[i]);
            y += lineHeight;
        }

        var promptY = panel.yMax - Padding - lineHeight;
        var scrollNote = _scrollBack > 0 ? $"   [scrolled back {_scrollBack}]" : "";
        GUI.Label(new Rect(x, promptY, innerWidth, lineHeight),
            $"> {_input}{(_caretOn ? "_" : " ")}{scrollNote}");
    }

    private void Submit()
    {
        var line = _input.Trim();
        _input = "";
        _historyIndex = -1;
        _scrollBack = 0;

        if (line.Length == 0)
            return;

        Print($"> {line}");
        History.Add(line);
        Commands.Execute(line, Print);
    }

    private void StepHistory(int direction)
    {
        if (History.Count == 0)
            return;

        if (_historyIndex < 0)
        {
            if (direction < 0)
                return;
            _historyIndex = History.Count - 1;
        }
        else
        {
            _historyIndex = Mathf.Clamp(_historyIndex - direction, 0, History.Count - 1);
        }

        _input = History[_historyIndex];
    }
}
