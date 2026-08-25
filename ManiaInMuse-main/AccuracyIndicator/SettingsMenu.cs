using UnityEngine;
using UnityEngine.EventSystems;

namespace AccuracyIndicator;

/// <summary>
/// 内置设置菜单：按 Insert 呼出。
/// 使用 Unity IMGUI（游戏内渲染，不做防录屏）绘制，全部参数实时生效，保存后写回 Player.cfg。
/// 菜单打开时禁用 EventSystem（鼠标点击无法作用于游戏），关闭时恢复。
/// </summary>
internal sealed class SettingsMenu
{
    private PlayerConfig _cfg;
    private bool _open;
    private Rect _windowRect = new(120, 80, 500, 620);

    // 拖动标题栏
    private bool _dragging;
    private Vector2 _dragOffset;

    // 游戏输入屏蔽
    private EventSystem _eventSystem;

    // 样式
    private GUIStyle _panelStyle;
    private GUIStyle _titleStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _valueStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _closeStyle;
    private bool _stylesReady;

    internal bool IsOpen => _open;

    /// <summary>强制关闭菜单并恢复游戏输入（场景切换/退出时调用）。</summary>
    internal void ForceClose()
    {
        if (!_open)
            return;

        _open = false;
        _dragging = false;
        try
        {
            if (_eventSystem != null)
            {
                _eventSystem.enabled = true;
                _eventSystem = null;
            }
        }
        catch { }
    }

    internal void Toggle()
    {
        _open = !_open;
        if (_open)
        {
            // 菜单打开时禁用游戏 UI 输入系统：鼠标无法与游戏交互
            try
            {
                _eventSystem = EventSystem.current;
                if (_eventSystem != null)
                    _eventSystem.enabled = false;
            }
            catch { }
        }
        else
        {
            // 恢复游戏 UI 输入
            try
            {
                if (_eventSystem != null)
                {
                    _eventSystem.enabled = true;
                    _eventSystem = null;
                }
            }
            catch { }
        }
    }

    /// <summary>在 MelonMod.OnGUI 中调用（Unity OnGUI 事件）。</summary>
    internal void OnGui()
    {
        if (!_open)
            return;

        if (_cfg == null)
            _cfg = PlayerConfig.LoadOrCreate();

        EnsureStyles();
        HandleDrag();

        // 面板
        GUI.Box(_windowRect, GUIContent.none, _panelStyle);

        GUILayout.BeginArea(new Rect(_windowRect.x + 14, _windowRect.y + 10, _windowRect.width - 28, _windowRect.height - 20));

        // 标题栏
        GUILayout.BeginHorizontal();
        GUILayout.Label("ManiaInMuse 设置", _titleStyle);
        if (GUILayout.Button("X", _closeStyle, GUILayout.Width(30), GUILayout.Height(26)))
            Toggle();
        GUILayout.EndHorizontal();
        GUILayout.Space(4);

        Section("基础");
        _cfg.FallTimeMs = SliderRow("下落时间", _cfg.FallTimeMs, 100f, 2000f, "{0:F0} ms");
        _cfg.OffsetMs = SliderRowInt("判定偏移", _cfg.OffsetMs, -1000, 1000, "{0} ms");
        _cfg.JudgementLinePosition = SliderRow("判定线位置", _cfg.JudgementLinePosition, 0f, 1f, "{0:P0}");

        Section("轨道");
        _cfg.TrackWidth = SliderRow("轨道宽度", _cfg.TrackWidth, 100f, 1600f, "{0:F0}");
        _cfg.TrackHeight = SliderRow("轨道高度", _cfg.TrackHeight, 200f, 2000f, "{0:F0}");
        _cfg.NoteWidth = SliderRow("音符宽度", _cfg.NoteWidth, 20f, 300f, "{0:F0}");
        _cfg.NoteHeight = SliderRow("音符高度", _cfg.NoteHeight, 20f, 300f, "{0:F0}");
        _cfg.PositionX = SliderRow("位置 X", _cfg.PositionX, -960f, 960f, "{0:F0}");
        _cfg.PositionY = SliderRow("位置 Y", _cfg.PositionY, -540f, 540f, "{0:F0}");

        Section("显示");
        _cfg.EnableJudgementFx = GUILayout.Toggle(_cfg.EnableJudgementFx, "判定特效", _labelStyle);
        _cfg.ShowCombo = GUILayout.Toggle(_cfg.ShowCombo, "连击数字", _labelStyle);
        _cfg.ShowApIndicator = GUILayout.Toggle(_cfg.ShowApIndicator, "AP 指示器", _labelStyle);

        GUILayout.Space(12);
        if (GUILayout.Button("保存并应用", _buttonStyle, GUILayout.Height(36)))
            _cfg.Save();
        GUILayout.Space(4);

        GUILayout.EndArea();
    }

    private void HandleDrag()
    {
        var e = Event.current;
        if (e == null)
            return;

        var titleRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 34);
        if (e.type == EventType.MouseDown && titleRect.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragOffset = e.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
        }
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            _windowRect.position = e.mousePosition - _dragOffset;
        }
        else if (_dragging && e.type == EventType.MouseUp)
        {
            _dragging = false;
        }
    }

    private void Section(string title)
    {
        GUILayout.Space(6);
        GUILayout.Label(title, _sectionStyle);
        GUILayout.Space(2);
    }

    private float SliderRow(string label, float value, float min, float max, string format)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _labelStyle, GUILayout.Width(120));
        float v = GUILayout.HorizontalSlider(value, min, max);
        GUILayout.Label(string.Format(format, v), _valueStyle, GUILayout.Width(100));
        GUILayout.EndHorizontal();
        return v;
    }

    private int SliderRowInt(string label, int value, int min, int max, string format)
    {
        return (int)Mathf.Round(SliderRow(label, value, min, max, format));
    }

    private void EnsureStyles()
    {
        if (_stylesReady)
            return;

        _panelStyle = new GUIStyle(GUI.skin.box);
        _panelStyle.normal.background = MakeTexture(24, 26, 38, 242);
        _panelStyle.border = new RectOffset(2, 2, 2, 2);

        _titleStyle = new GUIStyle(GUI.skin.label);
        _titleStyle.fontSize = 21;
        _titleStyle.fontStyle = FontStyle.Bold;
        _titleStyle.normal.textColor = new Color(0.92f, 0.95f, 1f);
        _titleStyle.alignment = TextAnchor.MiddleLeft;

        _sectionStyle = new GUIStyle(GUI.skin.label);
        _sectionStyle.fontSize = 15;
        _sectionStyle.fontStyle = FontStyle.Bold;
        _sectionStyle.normal.textColor = new Color(0.55f, 0.85f, 1f);

        _labelStyle = new GUIStyle(GUI.skin.label);
        _labelStyle.fontSize = 14;
        _labelStyle.normal.textColor = new Color(0.85f, 0.88f, 0.95f);

        _valueStyle = new GUIStyle(GUI.skin.label);
        _valueStyle.fontSize = 14;
        _valueStyle.alignment = TextAnchor.MiddleRight;
        _valueStyle.normal.textColor = Color.white;

        _buttonStyle = new GUIStyle(GUI.skin.button);
        _buttonStyle.fontSize = 16;
        _buttonStyle.fontStyle = FontStyle.Bold;
        _buttonStyle.normal.background = MakeTexture(46, 82, 190, 255);
        _buttonStyle.normal.textColor = Color.white;
        _buttonStyle.hover.background = MakeTexture(66, 102, 220, 255);
        _buttonStyle.hover.textColor = Color.white;
        _buttonStyle.active.background = MakeTexture(32, 58, 140, 255);
        _buttonStyle.active.textColor = Color.white;

        _closeStyle = new GUIStyle(GUI.skin.button);
        _closeStyle.fontSize = 15;
        _closeStyle.normal.background = MakeTexture(70, 30, 30, 255);
        _closeStyle.normal.textColor = Color.white;
        _closeStyle.hover.background = MakeTexture(110, 40, 40, 255);
        _closeStyle.hover.textColor = Color.white;

        _stylesReady = true;
    }

    private static Texture2D MakeTexture(int r, int g, int b, int a)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, new Color(r / 255f, g / 255f, b / 255f, a / 255f));
        tex.Apply();
        return tex;
    }
}
