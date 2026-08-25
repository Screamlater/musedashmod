using System.Diagnostics;
using System.Globalization;
using Il2CppInterop.Runtime.Attributes;
using Il2CppAssets.Scripts.PeroTools.Commons;

namespace AccuracyIndicator;

[RegisterTypeInIl2Cpp]
public class OsuPlayerHUD : MonoBehaviour
{
    private const float JudgementLineHeight = 4f;

    private readonly List<OsuPlayObject> _objects = new();
    private NativeOverlayWindow _overlay;
    private PlayerConfig _config;
    private JudgementFx _judgementFx;
    private IntPtr _gameHwnd;
    private bool _visible;
    private bool _dead;
    private bool _loggedMissingFile;
    private bool _loggedDrawError;
    private bool _loggedFrameInfo;

    public OsuPlayerHUD(IntPtr ptr) : base(ptr) { }

    private void Start()
    {
        _config = PlayerConfig.LoadOrCreate();

        // 判定特效（combo 显示独立于本对象，由 DrawFrame 每帧直读游戏连击）
        _judgementFx = new JudgementFx(_config.SkinDir)
        {
            Enabled = _config.EnableJudgementFx
        };
        _judgementFx.LoadSkin();

        // 用独立原生窗口渲染（不进入游戏帧缓冲，因此 Steam 录屏里看不到）
        _overlay = new NativeOverlayWindow();
        try
        {
            _overlay.Create(Screen.width, Screen.height);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[ManiaInMuse] Failed to create native overlay: {ex}");
            _overlay = null;
            return;
        }

        if (_objects.Count == 0)
            LoadLatestOsu();
    }

    [HideFromIl2Cpp]
    internal void LoadObjects(IReadOnlyList<OsuPlayObject> objects)
    {
        _objects.Clear();
        foreach (var obj in objects)
            _objects.Add(obj);

        _objects.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        MelonLogger.Msg($"[ManiaInMuse] osu player refreshed {_objects.Count} objects");
    }

    private void LoadLatestOsu()
    {
        _objects.Clear();
        string path = Path.Combine("UserData", "ManiaInMuse", "maps", "latest.osu");
        if (!File.Exists(path))
        {
            if (!_loggedMissingFile)
            {
                _loggedMissingFile = true;
                MelonLogger.Warning($"[ManiaInMuse] osu player could not find {path}");
            }
            return;
        }

        try
        {
            foreach (var obj in OsuPlayObjectReader.Read(path, _config))
                _objects.Add(obj);

            MelonLogger.Msg($"[ManiaInMuse] osu player loaded {_objects.Count} objects from {path}");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[ManiaInMuse] osu player failed to load {path}: {ex}");
        }
    }

    private void Update()
    {
        if (_dead || _overlay == null)
            return;

        // 推进歌曲时间（必须在绘制前调用，否则 SongTime 不会前进）
        Main.UpdatePlaybackState();

        bool show = Main.Active && Main.ShouldShowPlayerHud() && NativeOverlayWindow.IsProcessForeground();
        if (!show)
        {
            SetOverlayVisible(false);
            return;
        }

        if (_gameHwnd == IntPtr.Zero || !NativeOverlayWindow.IsWindowValid(_gameHwnd))
            _gameHwnd = GetGameWindowHandle();
        if (_gameHwnd == IntPtr.Zero)
        {
            SetOverlayVisible(false);
            return;
        }

        try
        {
            if (!_overlay.SyncToGameWindow(_gameHwnd))
            {
                SetOverlayVisible(false);
                return;
            }

            SetOverlayVisible(true);

            // 视觉时间（OffsetMs 正值让音符更晚落下）
            float visTime = Main.SongTime - _config.OffsetMs / 1000f;
            ConsumeJudgementEvents(visTime);

            int drawn = DrawFrame(visTime);

            if (!_loggedFrameInfo)
            {
                _loggedFrameInfo = true;
                MelonLogger.Msg($"[ManiaInMuse] Overlay ok: game=0x{_gameHwnd.ToInt64():X} client={_overlay.ClientWidth}x{_overlay.ClientHeight} objects={_objects.Count} drawn={drawn} t={Main.SongTime:F2}s");
            }
        }
        catch (Exception ex)
        {
            if (!_loggedDrawError)
            {
                _loggedDrawError = true;
                MelonLogger.Error($"[ManiaInMuse] Overlay frame failed: {ex}");
            }
            SetOverlayVisible(false);
        }
    }

    /// <summary>只在状态变化时 Show/Hide，避免每帧切换造成闪烁。</summary>
    private void SetOverlayVisible(bool visible)
    {
        if (_overlay == null || visible == _visible)
            return;

        _visible = visible;
        if (visible)
            _overlay.Show();
        else
            _overlay.Hide();
    }

    /// <summary>消费判定事件：对齐到轨道列，更新连击并生成特效。</summary>
    private void ConsumeJudgementEvents(float visTime)
    {
        if (_judgementFx == null)
        {
            lock (Main.JudgementEvents)
                Main.JudgementEvents.Clear();
            return;
        }

        List<JudgementEvent> events;
        lock (Main.JudgementEvents)
        {
            if (Main.JudgementEvents.Count == 0)
                return;
            events = new List<JudgementEvent>(Main.JudgementEvents);
            Main.JudgementEvents.Clear();
        }

        // 去重：同一音符的重复判定（Muse Dash 一次命中可能多次调用 AddScore/TriggerNoteMiss）
        // used 集合让双押（同 tick 不同音符）各自匹配到自己的轨道，重复判定（double）自然丢弃
        var used = new HashSet<int>();
        float lastFxTick = -999f;
        int lastFxLane = -1;

        foreach (var ev in events)
        {
            if (ev.Kind == JudgementEventKind.Miss)
            {
                // Miss 不要求精确对齐：用当前时间最近的预测音符定位，找不到就用中间轨道
                int lane = FindLaneForTick(ev.TickSec > 0 ? ev.TickSec : visTime, out _, used);
                if (lane < 1 || lane > _config.KeyCount)
                    lane = 2;

                float keyTime = ev.TickSec > 0 ? ev.TickSec : visTime;
                if (Math.Abs(keyTime - lastFxTick) < 0.05f && lane == lastFxLane)
                    continue;
                lastFxTick = keyTime;
                lastFxLane = lane;

                _judgementFx.AddHit(JudgementKind.Miss, lane, visTime);
            }
            else
            {
                int lane = FindLaneForTick(ev.TickSec, out bool aligned, used);
                if (!aligned)
                    continue; // 无新音符可匹配（重复判定）或对不上预测谱面，跳过

                if (Math.Abs(ev.TickSec - lastFxTick) < 0.05f && lane == lastFxLane)
                    continue;
                lastFxTick = ev.TickSec;
                lastFxLane = lane;

                _judgementFx.AddHit(MapResult(ev.Result), lane, visTime);
            }
        }
    }

    /// <summary>TaskResult -> 判定等级（Miss=1 Cool=2 Great=3 Prefect=4）。</summary>
    private static JudgementKind MapResult(int result)
    {
        return result switch
        {
            4 => JudgementKind.Perfect,
            3 => JudgementKind.Great,
            2 => JudgementKind.Cool,
            1 => JudgementKind.Miss,
            _ => JudgementKind.Great
        };
    }

    /// <summary>在预测谱面里找 tick 最接近的轨道（同时匹配 start/end，容差 0.5s）。
    /// used 排除已消费的音符：双押（同 tick 不同音符）依次匹配各自轨道，重复判定（double）自然匹配不到。</summary>
    private int FindLaneForTick(float tickSec, out bool aligned, HashSet<int> used)
    {
        aligned = false;
        if (_objects.Count == 0)
            return 1;

        int bestIdx = -1;
        int bestLane = 1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < _objects.Count; i++)
        {
            if (used.Contains(i))
                continue;

            var o = _objects[i];
            float d = Math.Min(Math.Abs(o.StartSec - tickSec), Math.Abs(o.EndSec - tickSec));
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
                bestLane = o.Lane;
            }
        }

        if (bestIdx >= 0 && bestDist <= 0.5f)
        {
            aligned = true;
            used.Add(bestIdx);
        }
        return bestLane;
    }

    private int DrawFrame(float songTime)
    {
        int winW = Math.Max(1, _overlay.ClientWidth);
        int winH = Math.Max(1, _overlay.ClientHeight);

        // 1920x1080 参考画布 -> 窗口像素（等价于原 CanvasScaler.ScaleWithScreenSize）
        float scale = Math.Min(winW / 1920f, winH / 1080f);
        float ox = (winW - 1920f * scale) * 0.5f;
        float oy = (winH - 1080f * scale) * 0.5f;
        float cx = 960f + _config.PositionX; // 轨道中心（画布坐标，x 向右）
        float cy = 540f + _config.PositionY; // 轨道中心（画布坐标，y 向下）

        _overlay.Clear();

        // 轨道背景
        _overlay.FillRect(
            ox + (cx - _config.TrackWidth * 0.5f) * scale,
            oy + (cy - _config.TrackHeight * 0.5f) * scale,
            _config.TrackWidth * scale,
            _config.TrackHeight * scale,
            _config.BackgroundColor.r, _config.BackgroundColor.g, _config.BackgroundColor.b, _config.BackgroundColor.a);

        // 判定线
        float lineY = JudgementLineY(_config);
        _overlay.FillRect(
            ox + (cx - _config.TrackWidth * 0.5f) * scale,
            oy + (cy - lineY - JudgementLineHeight * 0.5f) * scale,
            _config.TrackWidth * scale,
            JudgementLineHeight * scale,
            255, 255, 255, 255);

        // 音符（沿用原下落数学，仅把 Unity Image 换成原生矩形）
        float trackTopY = _config.TrackHeight * 0.5f;
        float trackBottomY = -_config.TrackHeight * 0.5f;
        float spawnY = trackTopY + _config.NoteHeight * 0.5f;
        float judgementY = lineY + _config.NoteHeight * 0.5f;

        int drawn = 0;
        for (int i = 0; i < _objects.Count; i++)
        {
            var obj = _objects[i];
            if (obj.EndSec < songTime - 0.1f)
                continue;
            if (obj.StartSec > songTime + _config.FallTimeSec)
                break;

            float headY = YForTime(obj.StartSec, songTime, spawnY, judgementY, _config.FallTimeSec);
            float tailY = obj.IsHold ? YForTime(obj.EndSec, songTime, spawnY, judgementY, _config.FallTimeSec) : headY;

            bool headVisible = headY >= trackBottomY - _config.NoteHeight && headY <= trackTopY + _config.NoteHeight;
            bool bodyVisible = obj.IsHold && Math.Max(headY, tailY) >= trackBottomY && Math.Min(headY, tailY) <= trackTopY;
            if (!headVisible && !bodyVisible)
                continue;

            float x = cx + _config.LaneToPlayfieldX(obj.Lane, _config.TrackWidth);

            // 长按身体（皮肤贴图平铺，缺失时回退纯色矩形）
            if (obj.IsHold && bodyVisible)
            {
                float lowerY = Mathf.Clamp(Math.Min(headY, tailY), trackBottomY, trackTopY);
                float upperY = Mathf.Clamp(Math.Max(headY, tailY), trackBottomY, trackTopY);
                float bodyHeight = Math.Max(0, upperY - lowerY);
                if (bodyHeight > 1)
                {
                    drawn++;
                    var bodyImg = _judgementFx?.GetNoteImage(obj.Lane, _config.KeyCount, NotePart.Body);
                    if (bodyImg != null)
                    {
                        // 平铺长条图（保持宽高比），从 head 底部向下铺到 tail 底部
                        float segH = _config.NoteWidth * bodyImg.Height / (float)bodyImg.Width;
                        float bodyTopCanvas = cy - upperY;
                        float bodyBottomCanvas = cy - lowerY;
                        for (float py = bodyTopCanvas; py < bodyBottomCanvas; py += segH)
                        {
                            float segBottom = Math.Min(py + segH, bodyBottomCanvas);
                            _overlay.DrawImage(bodyImg.Bgra, bodyImg.Width, bodyImg.Height,
                                ox + (x - _config.NoteWidth * 0.5f) * scale,
                                oy + py * scale,
                                _config.NoteWidth * scale,
                                (segBottom - py) * scale, 255);
                        }
                    }
                    else
                    {
                        _overlay.FillRect(
                            ox + (x - _config.NoteWidth * 0.5f) * scale,
                            oy + (cy - upperY) * scale,
                            _config.NoteWidth * scale,
                            bodyHeight * scale,
                            _config.HoldColor.r, _config.HoldColor.g, _config.HoldColor.b, _config.HoldColor.a);
                    }
                }
            }

            // 长按尾（贴图，底部对齐 tailY）
            if (obj.IsHold && tailY >= trackBottomY - _config.NoteHeight && tailY <= trackTopY + _config.NoteHeight)
            {
                var tailImg = _judgementFx?.GetNoteImage(obj.Lane, _config.KeyCount, NotePart.Tail);
                if (tailImg != null)
                {
                    float th = _config.NoteWidth * tailImg.Height / (float)tailImg.Width;
                    _overlay.DrawImage(tailImg.Bgra, tailImg.Width, tailImg.Height,
                        ox + (x - _config.NoteWidth * 0.5f) * scale,
                        oy + (cy - tailY - th) * scale,
                        _config.NoteWidth * scale, th * scale, 255);
                }
            }

            // 键头/点击键（贴图，缺失时回退纯色矩形）
            if (headVisible)
            {
                drawn++;
                var img = _judgementFx?.GetNoteImage(obj.Lane, _config.KeyCount, obj.IsHold ? NotePart.Head : NotePart.Tap);
                if (img != null)
                {
                    // 保持宽高比，底部对齐 headY（贴判定线）
                    float ih = _config.NoteWidth * img.Height / (float)img.Width;
                    _overlay.DrawImage(img.Bgra, img.Width, img.Height,
                        ox + (x - _config.NoteWidth * 0.5f) * scale,
                        oy + (cy - headY - ih) * scale,
                        _config.NoteWidth * scale, ih * scale, 255);
                }
                else
                {
                    Color32 noteColor = ColorForLane(obj.Lane, _config.NoteColor);
                    _overlay.FillRect(
                        ox + (x - _config.NoteWidth * 0.5f) * scale,
                        oy + (cy - headY - _config.NoteHeight * 0.5f) * scale,
                        _config.NoteWidth * scale,
                        _config.NoteHeight * scale,
                        noteColor.r, noteColor.g, noteColor.b, noteColor.a);
                }
            }
        }

        // 判定特效（画布坐标，写 CPU 画布，必须在 CopyBackBuffer 之前）
        if (_judgementFx != null)
        {
            _judgementFx.Render(_overlay, songTime,
                cx - _config.TrackWidth * 0.5f,
                cy - _config.TrackHeight * 0.5f,
                _config.TrackWidth,
                _config.TrackHeight,
                cy - lineY,
                scale, ox, oy, _config.KeyCount);
        }

        // 连击数字：每帧直读游戏实时连击，独立绘制（与判定事件完全解耦）
        if (_config.ShowCombo)
        {
            int combo = 0;
            try
            {
                var sb = StageBattleComponent.instance;
                if (sb != null)
                    combo = sb.GetCombo();
            }
            catch { }

            JudgementFx.DrawCombo(_overlay, combo,
                cx - _config.TrackWidth * 0.5f,
                cy - _config.TrackHeight * 0.5f,
                _config.TrackWidth,
                scale, ox, oy);
        }

        // AP 指示器：每帧监控游戏判定计数（全 Perfect 且无 Great/Cool/Miss 即为 AP）
        if (_config.ShowApIndicator)
        {
            bool ap = false;
            try
            {
                var tst = Singleton<TaskStageTarget>.instance;
                if (tst != null)
                    ap = tst.m_PerfectResult > 0 && tst.m_GreatResult == 0 && tst.m_CoolResult == 0 && tst.m_MissResult == 0;
            }
            catch { }

            JudgementFx.DrawApIndicator(_overlay, ap,
                cx - _config.TrackWidth * 0.5f,
                cy - _config.TrackHeight * 0.5f,
                _config.TrackWidth,
                scale, ox, oy);
        }

        _overlay.CopyBackBuffer();
        _overlay.Present();
        return drawn;
    }

    /// <summary>4K 模式下 2、3 轨的键头/点击键使用替代颜色（红色），其余轨道用正常颜色；长按身体不受影响。</summary>
    private Color32 ColorForLane(int lane, Color32 normal)
    {
        if (_config.UseLaneAltColor4K && _config.KeyCount == 4 && (lane == 2 || lane == 3))
            return _config.LaneAltColor;
        return normal;
    }

    private static float YForTime(float objectTime, float songTime, float topY, float bottomY, float fallTimeSec)
    {
        float untilHit = objectTime - songTime;
        float progress = 1f - untilHit / fallTimeSec;
        return Mathf.Lerp(topY, bottomY, progress);
    }

    private static float JudgementLineY(PlayerConfig config)
    {
        return config.TrackHeight * (0.5f - config.JudgementLinePosition);
    }

    private static IntPtr GetGameWindowHandle()
    {
        try
        {
            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (NativeOverlayWindow.IsWindowValid(hwnd))
                return hwnd;
        }
        catch { }

        return NativeOverlayWindow.FindProcessWindow();
    }

    private void OnDestroy()
    {
        _dead = true;
        _visible = false;
        _objects.Clear();

        if (_judgementFx != null)
        {
            _judgementFx.Reset();
            _judgementFx = null;
        }

        if (_overlay != null)
        {
            _overlay.Dispose();
            _overlay = null;
        }

        if (Main.PlayerHUD == this)
            Main.PlayerHUD = null;
    }
}

internal readonly struct OsuPlayObject
{
    internal readonly int Lane;
    internal readonly float StartSec;
    internal readonly float EndSec;
    internal readonly bool IsHold;
    internal readonly OsuPlayObjectKind Kind;

    internal OsuPlayObject(int lane, float startSec, float endSec, bool isHold, OsuPlayObjectKind kind = OsuPlayObjectKind.RegularTap)
    {
        Lane = lane;
        StartSec = startSec;
        EndSec = endSec;
        IsHold = isHold;
        Kind = kind;
    }

    internal bool IsLocalSwapCandidate => !IsHold && (Kind is OsuPlayObjectKind.RegularTap or OsuPlayObjectKind.BossTap);
    internal bool AllowsAnyPosture => Kind == OsuPlayObjectKind.BossTap;

    internal OsuPlayObject WithLane(int lane)
    {
        return new OsuPlayObject(lane, StartSec, EndSec, IsHold, Kind);
    }
}

internal enum OsuPlayObjectKind
{
    RegularTap,
    BossTap,
    Hold,
    Multi,
    UtilityTap,
    Imported
}

internal static class OsuPlayObjectReader
{
    internal static IEnumerable<OsuPlayObject> Read(string path, PlayerConfig config)
    {
        bool inHitObjects = false;
        var objects = new List<OsuPlayObject>();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inHitObjects = line.Equals("[HitObjects]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inHitObjects)
                    continue;

                var obj = ParseHitObject(line, config);
                if (obj.HasValue)
                    objects.Add(obj.Value);
            }
        }

        objects.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return objects;
    }

    private static OsuPlayObject? ParseHitObject(string line, PlayerConfig config)
    {
        string[] parts = line.Split(',');
        if (parts.Length < 5)
            return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x))
            return null;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int startMs))
            return null;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
            return null;

        int lane = config.XToLane(x);
        bool isHold = (type & 128) != 0;
        int endMs = startMs;
        if (isHold && parts.Length >= 6)
        {
            string endText = parts[5].Split(':')[0];
            if (!int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out endMs))
                endMs = startMs;
        }

        return new OsuPlayObject(lane, startMs / 1000f, Math.Max(startMs, endMs) / 1000f, isHold, OsuPlayObjectKind.Imported);
    }
}
