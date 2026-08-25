# ManiaInMuse

ManiaInMuse 是一个谱面读取、谱面转换和游戏内可视化播放器，用于 Muse Dash 的 MelonLoader Mod。

激活时会在进入歌曲时读取当前谱面的全部键时刻信息，导出原始 CSV，转换为 osu!mania 风格谱面，并在游戏内显示下落式谱面覆盖层。

可能与其他使用覆盖层的模组发生重叠冲突。

本Mod仅供练习使用，使用期间请离线游玩。切勿在官谱或者自制谱中使用模组并上传成绩。

> 本仓库是 [SanwuQian/ManiaInMuse](https://github.com/SanwuQian/ManiaInMuse) 的修改增强版，原项目版权归原作者所有，详见 [LICENSE](LICENSE)。

## 本版本特性（相对原版）

- **Steam 录屏无法捕捉**：下落覆盖层与判定特效渲染在独立的原生透明窗口中，通过 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 从 Windows 屏幕捕获中排除——**屏幕上正常显示，但 Steam 录屏/截图、OBS、Xbox Game Bar 等均无法捕捉到**。
- 读取游戏实时判定（`TaskStageTarget`），判定线处显示 osu!mania 风格判定特效（Perfect→300 / Great→200 / Cool→100 / Miss→0）。
- 轨道顶部显示**连击数字**（与游戏内连击实时同步）与 **AP 指示器**（全 Perfect 时金色高亮）。
- 音符与长按使用皮肤贴图（4K 内两轨 `mania-note2` 系列，外两侧 `mania-note1` 系列，长按头/身/尾完整贴图）。
- 按 `Insert` 呼出**内置设置菜单**（游戏内 IMGUI）：下落时间、判定偏移（`OffsetMs`）、轨道/音符尺寸与位置、显示开关均可实时调整并保存，无需手动编辑配置文件。
- 仅支持 4K 键数。

## 功能

- 进入歌曲时从 `StageBattleComponent` 读取当前 Muse Dash 谱面数据。
- 导出原始谱面 CSV，包含时间、类型、空中/地面、BPM、长按长度、multi 连击参数等字段。
- 为当前歌曲生成 `latest.osu`。
- 在游戏内显示黑色背景的下落式谱面覆盖层。
- 仅支持 4K 键数。
- 支持通过 `Player.cfg` 配置每个轨道对应空中或地面。
- 支持 `monster`、`ghost`、`hold`、`boss`、`multi`、`music`、`block` 等类型。
- 对 multi 使用基于 BPM 的左右对拍生成规则。
- 使用局部轨道交换和短间隔修复，尽量减少不顺手的密集同轨间隔。
- 暂停、结算、失败、退出歌曲后会隐藏播放器界面。
- 读取游戏实时判定，在判定线处显示 osu!mania 风格判定特效（`otya's Mania 蓝白块` 皮肤），轨道顶部显示连击数。
- 按 `Insert` 呼出内置设置菜单，所有显示参数均可实时调整并保存。

## 运行环境

- Muse Dash，Il2Cpp 版本。
- MelonLoader `0.7.3` net6 运行环境。
- 如果需要从源码编译，需要安装 .NET 6 SDK。

ManiaInMuse 本身不强依赖 MuseDashMirror 或 CustomAlbums。如果你要游玩自定义专辑，CustomAlbums 等 Mod 仍然需要按它们自己的要求安装。

## 安装

把编译得到的 DLL 和配置文件放到 Muse Dash 目录：

```text
Muse Dash/
  Mods/
    ManiaInMuse.dll
  UserData/
    ManiaInMuse/
      Player.cfg
```

如果 `Player.cfg` 不存在，Mod 第一次运行时会自动创建默认配置。

### 判定特效皮肤

仓库自带精简版皮肤目录 `otya's Mania 蓝白块/`（osu!mania 判定与 note 贴图）。把它**复制**到游戏目录：

```text
Muse Dash/UserData/ManiaInMuse/skin/   ← 复制 otya's Mania 蓝白块 里的文件到这里
```

皮肤文件来源为 osu! 社区皮肤 **otya's Mania 蓝白块**，版权归皮肤原作者所有；本仓库仅附带与 Mod 相关的精简素材（判定图标与 note 贴图），请勿将精简包当作完整皮肤使用。如原作者要求移除，本仓库将立即删除相关素材。

> 注意：精简包不含 `mania-hit200.png`（Great 判定图标），缺失时 Great 判定特效会回退为纯色块；如需完整判定图标，请自行从 osu! 皮肤站获取原皮肤后放入 `skin/` 目录。

## 导出文件

每次进入歌曲后，ManiaInMuse 会把谱面文件写入：

```text
Muse Dash/UserData/ManiaInMuse/maps/
```

生成的文件：

- `latest.csv`：最近一次进入歌曲导出的原始谱面数据。
- `latest.osu`：最近一次转换得到的 osu!mania 谱面。
- `yyyyMMdd_HHmmss_fff_<noteCount>_notes.csv`：带时间戳的历史 CSV 导出缓存。

历史 CSV 会按默认缓存策略自动清理，避免目录无限增长。

## Player.cfg 配置

默认配置示例：

```ini
[Player]
FallTimeMs = 480
TrackWidth = 480
TrackHeight = 1080
NoteWidth = 120
NoteHeight = 80
PositionX = 0
PositionY = 0
BackgroundColor = 0,0,0,255
NoteColor = 0,220,70,255
HoldColor = 110,110,110,255
LaneColor4K = 255,60,60,255
JudgementLinePosition = 1
KeyCount = 4

[keys:4]
LaneTypes = A,G,A,G
Split = 2
```

参数含义：

- `OffsetMs`：视觉判定偏移，单位毫秒，范围 `-1000 ~ 1000`。正值让音符更晚落到判定线。
- `FallTimeMs`：键从顶部生成到判定线的下落时间，单位毫秒。
- `TrackWidth`、`TrackHeight`：覆盖层轨道区域的宽高，基于 1920x1080 参考画布。
- `NoteWidth`、`NoteHeight`：点击键方块的宽高。长按头使用同样大小，长按身体使用 `NoteWidth`。
- `PositionX`、`PositionY`：轨道区域相对屏幕中心的偏移。
- `BackgroundColor`：轨道背景颜色，格式为 `R,G,B,A`，范围 `0-255`。
- `NoteColor`：点击键和长按头的颜色。
- `HoldColor`：长按身体的颜色。
- `LaneColor4K`：4K 模式下中间两轨（2、3 轨）的音符颜色，格式 `R,G,B,A`；1、4 轨仍使用 `NoteColor`。填 `none` 可关闭该覆盖。
- `EnableJudgementFx`：是否显示判定特效（判定文字 + 闪光），默认 `true`。
- `ShowCombo`：是否在轨道顶部显示连击数，默认 `true`。
- `SkinDir`：判定特效皮肤图片目录，默认 `UserData\ManiaInMuse\skin`。需要 `mania-hit300.png`、`mania-hit300-0.png`（Perfect 闪光）、`mania-hit200.png`（Great）、`mania-hit100.png`（Cool）、`mania-hit0.png`（Miss），以及 note 贴图 `mania-note1/1H/1L/1T.png`、`mania-note2/2H/2L/2T.png`（4K 内两轨用 note2，外两侧用 note1）。`otya's Mania 蓝白块` 文件夹可直接作为皮肤目录。
- `JudgementLinePosition`：判定线在轨道区域内的相对位置。`0` 是顶部，`0.5` 是中间，`1` 是底部。
- `KeyCount`：固定为 `4`，本版本仅支持 4K 模式。
- `[keys:4] LaneTypes`：指定 4K 模式下每个轨道对应空中或地面。`A` 表示空中，`G` 表示地面。
- `[keys:4] Split`：左半区轨道数量，用于 multi 的左右对拍分配。

以上参数均可在游戏内按 `Insert` 呼出的设置菜单中调整（实时生效，点"保存并应用"写回 `Player.cfg`），无需手动编辑配置文件。

## 谱面类型映射

| Type | 名称 | 处理方式 |
| --- | --- | --- |
| `1` | `monster` | 普通点击 |
| `2` | `block` | 检查是否会被障碍命中，必要时插入躲避键 |
| `3` | `hold` | 有持续时间的长按 |
| `4` | `ghost` | 按普通点击处理 |
| `5` | `boss` | 点击一次即可，空中或地面都可以 |
| `6` | `energy` | 按人物所在空中/地面位置收集 |
| `7` | `music` | 按人物所在空中/地面位置收集 |
| `8` | `multi` | 根据 BPM 生成连续对拍或重复点击 |

在 multi 持续期间，转换器会忽略其他类型的 note，因为 Muse Dash 规则中 multi 期间不需要单独处理这些对象。

## 编译

项目会引用本地 Muse Dash 目录中由 MelonLoader 生成的程序集：

```xml
<MuseDashPath>D:\APP Profile\steam\steamapps\common\Muse Dash</MuseDashPath>
```

如果 Muse Dash 安装路径不同，可以在编译时传入 `MuseDashPath`：

```powershell
dotnet build "AccuracyIndicator\AccuracyIndicator.csproj" -c Release -p:MuseDashPath="你的 Muse Dash 目录"
```

该目录必须安装 MelonLoader `0.7.3`；项目会在编译前检查 `MelonLoader.dll` 的版本。

编译命令：

```powershell
dotnet build "D:\_1 Resourse\_Tool\musedash\mods\ManiaInMuse\AccuracyIndicator\AccuracyIndicator.csproj" -c Release
```

DLL 输出路径：

```text
D:\_1 Resourse\_Tool\musedash\mods\ManiaInMuse\debug\ManiaInMuse.dll
```

## 目录结构

- `AccuracyIndicator/`：主 Mod 源码。命名空间仍保留历史名称，但程序集名和 Mod 名是 `ManiaInMuse`。
- `OsuGenerator/`：独立 CSV 转 osu 的原型工具。
- `DirectOsuPlayer/`：早期用于验证播放器显示效果的浏览器原型。
- `otya's Mania 蓝白块/`：判定特效皮肤素材（精简版，来自 osu! 社区皮肤，版权归原作者）。

## 当前版本

`2.1.0`（基于原版 `2.0.0` 的增强版：Steam 录屏不可捕捉叠加层、实时判定特效、连击/AP 指示器、内置设置菜单、4K 皮肤贴图）

`2.0.0`
