namespace AccuracyIndicator;

internal enum JudgementKind
{
    Perfect, // 300
    Great,   // 200
    Cool,    // 100
    Miss     // 0（Lost）
}

internal enum NotePart
{
    Tap,
    Head,
    Body,
    Tail
}

/// <summary>
/// 判定特效与连击显示。
/// 使用 osu!mania 皮肤图片（判定文字 mania-hitXXX.png + 闪光 mania-hitXXX-0.png），
/// 渲染进原生覆盖窗口（不进入游戏帧缓冲，因此不会被 Steam 录屏）。
/// </summary>
internal sealed class JudgementFx
{
    private const float FxDuration = 0.55f;

    private sealed class FxInstance
    {
        internal JudgementKind Kind;
        internal int Lane;
        internal float StartSongTime;
    }

    private readonly List<FxInstance> _active = new();

    private readonly Dictionary<JudgementKind, PngImage> _judgeText = new();
    private readonly Dictionary<JudgementKind, PngImage> _judgeFlash = new();

    // note 贴图（4K：1/4 轨 note1 系列，2/3 轨 note2 系列）
    private PngImage _note1, _note1H, _note1L, _note1T;
    private PngImage _note2, _note2H, _note2L, _note2T;

    private readonly string _skinDir;

    internal bool Enabled = true;
    internal int LoadedImages { get; private set; }

    internal JudgementFx(string skinDir)
    {
        _skinDir = skinDir;
    }

    /// <summary>加载判定图片与 note 贴图（缺失时回退纯色矩形渲染）。</summary>
    internal void LoadSkin()
    {
        _judgeText.Clear();
        _judgeFlash.Clear();
        _note1 = _note1H = _note1L = _note1T = null;
        _note2 = _note2H = _note2L = _note2T = null;
        LoadedImages = 0;

        try
        {
            if (!Directory.Exists(_skinDir))
            {
                MelonLogger.Warning($"[ManiaInMuse] Judgement skin dir not found: {_skinDir}");
                return;
            }

            // 判定图标映射：Perfect->300(+闪光)、Great->200、Cool->100、Miss->0
            LoadPair(JudgementKind.Perfect, "mania-hit300.png", "mania-hit300-0.png");
            LoadPair(JudgementKind.Great, "mania-hit200.png", null);
            LoadPair(JudgementKind.Cool, "mania-hit100.png", null);
            LoadPair(JudgementKind.Miss, "mania-hit0.png", null);

            // note 贴图
            TryLoad("mania-note1.png", img => _note1 = img, null);
            TryLoad("mania-note1H.png", img => _note1H = img, null);
            TryLoad("mania-note1L.png", img => _note1L = img, null);
            TryLoad("mania-note1T.png", img => _note1T = img, null);
            TryLoad("mania-note2.png", img => _note2 = img, null);
            TryLoad("mania-note2H.png", img => _note2H = img, null);
            TryLoad("mania-note2L.png", img => _note2L = img, null);
            TryLoad("mania-note2T.png", img => _note2T = img, null);

            MelonLogger.Msg($"[ManiaInMuse] Judgement skin loaded: {LoadedImages} images from {_skinDir}");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[ManiaInMuse] Failed to load judgement skin: {ex}");
        }
    }

    /// <summary>
    /// 获取轨道对应的 note 贴图。4K 模式：1、4 轨用 note1 系列，2、3 轨用 note2 系列；
    /// 其他键数全部用 note1 系列。图片缺失返回 null（调用方回退纯色矩形）。
    /// </summary>
    internal PngImage GetNoteImage(int lane, int keyCount, NotePart part)
    {
        bool use2 = keyCount == 4 && (lane == 2 || lane == 3);
        return part switch
        {
            NotePart.Tap => use2 ? _note2 : _note1,
            NotePart.Head => use2 ? _note2H : _note1H,
            NotePart.Body => use2 ? _note2L : _note1L,
            NotePart.Tail => use2 ? _note2T : _note1T,
            _ => null
        };
    }

    private void LoadPair(JudgementKind kind, string textName, string flashName)
    {
        TryLoad(textName, img => _judgeText[kind] = img, kind);
        if (flashName != null)
            TryLoad(flashName, img => _judgeFlash[kind] = img, kind);
    }

    private void TryLoad(string fileName, Action<PngImage> store, JudgementKind? kind)
    {
        try
        {
            string path = Path.Combine(_skinDir, fileName);
            if (!File.Exists(path))
            {
                if (kind.HasValue)
                    MelonLogger.Warning($"[ManiaInMuse] Judgement skin missing: {fileName} ({(int)kind.Value})");
                return;
            }

            var img = PngImage.Load(path);
            store(img);
            LoadedImages++;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[ManiaInMuse] Failed to load {fileName}: {ex.Message}");
        }
    }

    internal void AddHit(JudgementKind kind, int lane, float songTime)
    {
        if (!Enabled)
            return;

        _active.Add(new FxInstance { Kind = kind, Lane = lane, StartSongTime = songTime });

        // 限制同时存在的特效数量，防止列表无限增长
        while (_active.Count > 24)
            _active.RemoveAt(0);
    }

    internal void Reset()
    {
        _active.Clear();
    }

    /// <summary>
    /// 渲染判定特效。坐标使用 1920x1080 画布系（y 向下）。
    /// 只显示判定文字图（不叠加 -0 闪光图，避免同一判定出现两张图片）。
    /// </summary>
    internal void Render(NativeOverlayWindow overlay, float songTime,
        float trackLeft, float trackTop, float trackWidth, float trackHeight,
        float judgementCanvasY, float canvasToPixels, float offsetX, float offsetY, int laneCount)
    {
        // ---- 判定特效 ----
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var fx = _active[i];
            float age = songTime - fx.StartSongTime;
            if (age < 0)
                continue;
            if (age >= FxDuration)
            {
                _active.RemoveAt(i);
                continue;
            }

            float t = age / FxDuration;
            byte alpha = (byte)(255f * (1f - t * t)); // 缓慢淡出

            // 判定文字图：判定线上方约 90 画布单位
            if (_judgeText.TryGetValue(fx.Kind, out var textImg))
            {
                float h = 64f;
                float w = h * textImg.Width / (float)Math.Max(1, textImg.Height);
                float cx = LaneCenterX(fx.Lane, trackLeft, trackWidth, laneCount);
                float cy = judgementCanvasY - 90f - h * 0.5f;
                overlay.DrawImage(textImg.Bgra, textImg.Width, textImg.Height,
                    offsetX + (cx - w * 0.5f) * canvasToPixels,
                    offsetY + (cy - h * 0.5f) * canvasToPixels,
                    w * canvasToPixels, h * canvasToPixels, alpha);
            }
        }
    }

    /// <summary>
    /// 绘制连击数字（轨道顶部居中：大号数字 + COMBO 小字，osu!mania 风格）。
    /// 独立静态方法：combo 值由调用方每帧直接从游戏读取，不依赖任何事件状态。
    /// </summary>
    internal static void DrawCombo(NativeOverlayWindow overlay, int combo,
        float trackLeft, float trackTop, float trackWidth,
        float canvasToPixels, float offsetX, float offsetY)
    {
        if (combo < 1 || combo > 99999)
            return;

        string text = combo.ToString();
        int fontSize = 84;
        float comboY = trackTop + 6f;
        float centerX = offsetX + (trackLeft + trackWidth * 0.5f) * canvasToPixels;
        float shY = offsetY + comboY * canvasToPixels;
        int textW = overlay.MeasureTextWidth(text, fontSize);
        float startX = centerX - textW * 0.5f;

        // 数字（阴影 + 本体）
        overlay.DrawGdiText(text, startX + 4, shY + 4, fontSize, 0, 0, 0);
        overlay.DrawGdiText(text, startX, shY, fontSize, 255, 255, 255);

        // COMBO 小字
        const string comboLabel = "COMBO";
        const int labelFont = 22;
        int lw = overlay.MeasureTextWidth(comboLabel, labelFont);
        float ly = shY + fontSize * 0.74f + 4f;
        overlay.DrawGdiText(comboLabel, centerX - lw * 0.5f + 2f, ly + 2f, labelFont, 0, 0, 0);
        overlay.DrawGdiText(comboLabel, centerX - lw * 0.5f, ly, labelFont, 185, 196, 220);
    }

    /// <summary>
    /// 绘制 AP 指示器（轨道顶部右侧徽章）：AP 时金色高亮，非 AP 时灰色暗显。
    /// AP 状态由调用方每帧从游戏判定计数读取。
    /// </summary>
    internal static void DrawApIndicator(NativeOverlayWindow overlay, bool ap,
        float trackLeft, float trackTop, float trackWidth,
        float canvasToPixels, float offsetX, float offsetY)
    {
        const string label = "AP";
        const int fontSize = 28;

        float cx = offsetX + (trackLeft + trackWidth) * canvasToPixels;
        float cy = offsetY + (trackTop + 10f) * canvasToPixels;
        int w = overlay.MeasureTextWidth(label, fontSize);

        if (ap)
        {
            // 金色徽章
            overlay.FillRect(cx - w - 18f, cy, w + 22f, 38f, 82, 66, 12, 230);
            overlay.DrawGdiText(label, cx - w - 14f + 2f, cy + 5f + 2f, fontSize, 0, 0, 0);
            overlay.DrawGdiText(label, cx - w - 14f, cy + 5f, fontSize, 255, 205, 60);
        }
        else
        {
            // 灰色暗显（监控中，未达成 AP）
            overlay.FillRect(cx - w - 18f, cy, w + 22f, 38f, 34, 34, 40, 170);
            overlay.DrawGdiText(label, cx - w - 14f, cy + 5f, fontSize, 96, 96, 108);
        }
    }

    private static float LaneCenterX(int lane, float trackLeft, float trackWidth, int laneCount)
    {
        int count = Math.Max(1, laneCount);
        float slot = trackWidth / count;
        int clamped = Math.Clamp(lane, 1, count);
        return trackLeft + slot * (clamped - 0.5f);
    }
}
