# 手套（Glove）

在**除「我是僵尸 / 我是僵尸大冒险」以外**的关卡里自动启用游戏自带的**搬运手套**。
原版手套只在调试开关（`CommandManager.debugOpenGlove`）下才会出现，本 Mod 把它做成了正常玩法。

- mod id：`glove`　name：`手套`　version：`1.0.6`
- 程序集身份：`JTYGlove`（包内固定路径 `Runtime/ModAssembly.dll`）
- 入口类型：`GloveEntry`

## 功能

| 功能 | 说明 |
|---|---|
| 启用手套 | 战斗中轮询：`currentLevelConfig != null`、非 IZM/IZM2、`GetGloveFeature() == null` ⇒ `control.AddFeature("Glove", new Dictionary())`，随后**补跑** `GameInit()` / `GameStart()`（战斗中途加入时游戏不会替我们跑这两步）。幂等、失败只 Warn 一次。 |
| IZM 排除 | `TowerDefenseManager.IsIZMMode()` / `IsIZM2Mode()` 为真 ⇒ **完全不动**（与游戏自己 `GloveManager._Input` 的排除条件逐字一致）。 |
| 按钮位置（v1.0.5：只有一套） | **横屏竖屏通用**一套 `landMode/landX/landY`。`auto` = 保持游戏原位置；`tl…br` = 九宫格；`custom` = 百分比坐标。v1.0.5 已删掉原来「竖版」那套设置（玩家反馈无效）。 |
| 战斗设置页 | Mod 按钮列（`ModCol4`，与其它 Mod 入口同列）注入「手套 · 设置」→ 打开**本 Mod 独占**浮层面板，内含**两行**：「位置」（共享组件 `PosEditorRow.PanelSpec`）+「搬运 · 底座」（v1.0.6，CheckBox）。改动即时生效 + 写配置。 |
| ★ 连底座移动（v1.0.6） | 配置项 `moveWithBase`（默认开）。植物本体按宿主口径放不到目的地（`CanPacketPlant == false`）时，若它脚下带着**符合目的地地形**的花盆 / 莲叶 / 花盆莲叶，就把底座一起搬过去再放植物；两层任一步不成立 ⇒ 底座原地撤回，**什么都不变**。目的地本来就能种时**完全不接管**（宿主原逻辑）。 |
| 收款码 | 每帧 `SponsorQrInjector.Tick(tree, "手套", PackageRoot, "Resources/SponsorQR.png")`；`aboutAuthor` 首行以 `手套` 逐字开头（注入器靠它判定归属）。 |
| ★ 手套模式抑制全场植物点击（v1.0.4） | `glovePick == true` 期间，**全场植物**的 `MousePressComponent` 一律 `SetAlive(false)`（激活时扫全场、每 3 帧补悬停那株、每 0.5s 重扫），关闭/换关/卸载全部还原 ⇒ 泡椒罐子、加农炮不会被「拿起」或「放下」那一次点击触发。刚放下的那一株额外留 0.25s 窗口；拿起瞬间还会撤销植物身上可能已触发的 `pressed` / `pressAwait`。 |
| ★ 预览用本体外观（v1.0.3） | 拖动预览不再是「按 config.name 重造默认外观」，而是 `pickedCharacter.sprite.Duplicate()` 的克隆（并补齐 `flashAnimeData` / `atlasProfileOverride` / `mediaReplaceAtlas*` 等非导出状态）⇒ 毁灭加农炮等变体显示本体外观；克隆不可用会自动回退宿主默认预览并告警。 |
| ★ 铲子·手套互斥（v1.0.2） | 宿主 `PacketPickControl.ProcessTools`（:1221-1251）只处理「选卡 vs 工具」，**不管工具之间互斥** ⇒ 以前能同时选中铲子和手套。现在做**上升沿互斥**：手套刚选中就 `ShovelReset()` 取消铲子；铲子刚选中就 `GloveReset()` 取消手套；同帧双变 true 时按刚按下的动作判先后，判不出则保留手套。 |
| ★ 铲子白色遮罩（v1.0.1） | 铲子选中某株植物时，每帧写 `SetSpriteGroupShaderParameter("whiteStrength", 1f)`（shader 里 `COLOR += whiteStrength`）⇒ 整株纯白；离开写回 0。配置项 `shovelWhiteMask` 关掉后完全不干预（且保证清零）。 |

## 实现要点

### 铲子白色遮罩（`TickShovelMask`）

- **判据**：`ShovelManager.shovelPick`（铲子激活）+ 公开字段 `ShovelManager.lastShovelPlant`
  （`ProcessShovelPick` 里就是"当前被铲子选中的角色"）；取不到时按 `ProcessShovelPick` 的同一套公式
  自己算一次（`GetShovelCharacter((mouse − GetMapCellPos(grid) + (0, groundHeight)).Y / GetMapGridSize().Y)`）。
  只对 `TowerDefensePlant` 生效（墓碑/坑/护盾不加白）。
- **为什么每帧维持**：`HitFlashComponent`（`HitFlashComponent.cs:443`）在受击闪白时会把**它自己那份**
  `_whiteStrength` 写进同一个 shader 参数 ⇒ 我们的 1.0 会被覆盖回 0，所以悬停期间每帧写一次。
- **还原**：换目标 / 离开 / 关开关 / 卸载 都写回 0（用 `GetInstanceId()` 判"是不是同一株"）。

### 铲子 / 手套 互斥（`TickToolMutex`）

- **为什么宿主不管**：铲子、手套是 `PacketPickControl.tools` 里两个独立工具（`ShovelPickTool` / `GlovePickTool`），
  `ProcessTools`（`PacketPickControl.cs:1221-1251`）只做"选了卡就把工具释放"，**从不处理工具之间互斥**
  ⇒ 两个 `IsPicking()` 能同时为 true，玩家就能处于"铲子模式 + 手套模式"。
- **做法**：每帧读 `gloveFeature.glovePick` / `shovelManager.shovelPick`，用**沿状态**判"谁刚被选中"：
  - 手套上升沿 ⇒ `CancelShovelTool`：`ShovelReset()`（清 `shovelPick`、按钮取消按下、藏地图铲子）
    ＋兜底 `PickShovel(false)`（仅在仍 picking 时）＋`shovelButton.SetPressedNoSignal(false)`；
  - 铲子上升沿 ⇒ `CancelGloveTool`：`ClearPickedState()` ＋ `GloveReset()` ＋ `gloveManager.SetGloveButtonPressed(false)`；
  - 同帧双上升沿 ⇒ 用本帧刚触发的 `Glove`/`Shovel` 动作判先后，判不出则保留手套、取消铲子；
  - 两个都为 true 但都不是本帧的沿（残局）⇒ 只处理一次，保守取消手套、保留原生铲子。
- **沿状态复位**：用 `Feature`/`ShovelManager` 的 `GetInstanceId()` 变化检测换关/重开（实例重建），
  避免上一关的 `true` 被当成本帧上升沿；离开战斗时也 `ResetMutexEdges()`。
- 日志（受 `enableLog` 门控）：「手套与铲子互斥：已取消铲子（…）」「…已取消手套（…）」。

### 手套模式期间抑制全场植物点击（`TickClickSuppression`，v1.0.4）

- **为什么 v1.0.3「拿到 pickedCharacter 后再屏蔽」必然晚一拍**：Godot 的 `_input`/`_unhandled_input`
  派发在每帧 `process_frame` **之前**，而"拿起某株植物"这一次 press 与 `ProcessGloveSelect → SelectCharacter`
  是**同一批输入**。泡椒罐（`TowerDefensePlantJalaVase.cs:75`）直接 `OnPressed += HandleVasePressed`（按下即砸），
  加农炮同样挂 `OnPressed/OnFinishPressed` ⇒ 等拿到 `pickedCharacter` 再 `SetAlive(false)` 已经晚了。
- **现在的语义**：`glovePick == true`（手套模式）期间，**所有植物**的 `MousePressComponent` 一律 `SetAlive(false)`：
  - 激活沿：`characterRegistry.GetCharactersIntersectingRectList(new Rect2(-10000,-10000,20000,20000))`
    枚举全场 → `is TowerDefensePlant` → `componentManager.GetRuntime<MousePressComponent>()`
    → **原本 `Alive == true` 才关**并记进集合（instanceId → 组件 + 原本是否活着 + 窗口）；
  - 维护：每 3 帧对**鼠标悬停格那株**再确保一次（`CannonComponent` 会自己 `SetAlive(...)` 改回来），
    每 500ms 重扫全场（手套开着时新种下的植物也要屏蔽）；
  - 退出沿 / 换关 / 卸载：`RestoreAllSuppressedForce` 把集合里"我们关掉且原本活着"的全部 `SetAlive(true)`；
  - 刚放置的那一株额外保留 `CarryClickGuardMsec = 250ms` + 确认键已松开的窗口（`RestoreExpiredSuppressions`）。
- ⚠️ `GetCharactersIntersectingRectList` 返回的是注册表**内部复用缓冲**（`_rectQueryBuffer`），
  下次查询会被覆盖、且 `SetAlive` 会回调宿主 ⇒ 先复制到自有 `_scanScratch` 再遍历。
- **兜底（Lead 追加）**：`UndoAccidentalPress` 在**刚拿起**的那一帧反射检查该植物身上
  **名字恰为 `pressed` / `pressAwait` 的 public bool 字段**并置回 false（泡椒罐就有这两个），
  同时若 `MousePressComponent.IsPressed` 为真则 `SetAlive(false)`（会走 `OnAliveChanged → ResetInteraction` 清按下态）。
  一次性、不每帧刷，失败静默。
  ⚠️ 静态核查发现：泡椒罐的"砸"是 **动画驱动** —— press 时已 `_hammer.SetAnimation("OpenPot", loop:false)`，
  而 `HammerAnimeCompleted("OpenPot")` 会**无条件 `Destroy()`**（`TowerDefensePlantJalaVase.cs:257-264`），
  所以单靠清 `pressed` 救不回来；真正拦住它的是"点击根本没传到植物"（即上面的全场抑制）。
- 屏蔽只拦"点击"：不影响手套拾取（走 `GetShovelCharacter/CanShovel`）、不影响受击与正常战斗逻辑。
  日志（`enableLog`）：「手套模式：已屏蔽 N 株植物的点击」/「手套模式：已还原 N 株植物点击」/
  「取不到 MousePressComponent：<名字>」（按名字只打一次）/「拿起时撤销误触发的按键状态：<名字>」。

### 预览用本体外观（`TickRealPreview`）

- 宿主 `GloveManager.CreatePreviewSprite` 走 `TowerDefenseManager.GetCharacterSprite(config.name)`，
  **按名字重造默认精灵场景** ⇒ 变体（毁灭加农炮 / 皮肤 / 杂交形态）显示成基础形态。
- 本 Mod 改为：`pickedCharacter.sprite.Duplicate()` → 挂到 `mapControl.spriteNode`，沿用宿主的
  `Modulate.a = 0.5` + `meshColor.a = 0.5`、`ZIndex = 1000`、`LightMask = 0`，每帧**照抄宿主 Process 的定位**
  （能放 ⇒ `GetMapCellPlantPos(目标格)`；不能放 ⇒ `spriteNode.GetLocalMousePosition() - (0,20)`）。
- ⚠️ `Duplicate()` 只复制 `[Export]` 属性 ⇒ `CopyPreviewVisualState()` 再补 `flashAnimeData` /
  `atlasProfileOverride` / `mediaReplaceAtlas*` / `frameIndex` / `clipRange` 等运行时状态。
- ⚠️ 克隆是否真能渲染**只能在运行时确认** ⇒ 建好后下一帧做一次健康检查
  （本体 `canvasItem`/`meshTexture` 有效而克隆都无效 ⇒ 判失败），失败就丢掉克隆、
  `gloveManager.CreatePreviewSprite()` 恢复宿主默认预览，并打一条明确 Warn（本关卡不再重试）。
- 只有克隆建成功后，才 `gloveManager.FreePreviewSprite()` 释放宿主那个默认外观预览
  （宿主 `Process` 随后会因 `IsInstanceValid(null)` 提前 return，不再覆盖我们的定位）。

### 连底座移动（`TickMoveWithBase` / `TryPlaceWithSupport`，v1.0.6）

> 玩家原话：「手套mod增加一个设置……如果要移动的植物无法种植在移动目的地，如果移动的该植物带符合目的情况的
> 花盆或莲叶或花盆莲叶（一种两栖植物，同时拥有花盆和莲叶的功能），是否连同花盆或莲叶或花盆莲叶一起移动过去？」

- **为什么是"兜底"而不是"接管"**：宿主 `ProcessGlovePlace`
  （`unpack/Registry/Battle/Feature/Glove/TowerDefenseBattleFeatureGlove.cs:195-218`）只有在
  `cell.CanPacketPlant(pickedCharacter.packet) == true` 时才 `PlaceCharacter`，为 false 时**什么都不做**
  ⇒ "带上底座能不能放"这个判断宿主里根本不存在。所以本 Mod 只在"宿主已经决定不放"之后补一次尝试：
  `CanPacketPlant == true` ⇒ 第一行就 `return`，仍由宿主自己放（它的 tween / 音效 / 联机同步全不动）。
- **判据全部复用宿主（不自己发明规则）**：
  | 问题 | 用的宿主判据 |
  |---|---|
  | 底座符不符合目的地 | `targetCell.CanPacketPlant(底座.packet)`（`TowerDefenseCellInstance.cs:751`） |
  | 植物本体能不能种在（已搬好底座的）目的地 | `targetCell.CanPacketPlant(植物.packet)`，同上 |
  | 这株植物脚下是不是真的带底座 | `XWModPlantPlacementService.IsSupport(config)` ⇔ `(config.physiqueTypeFlags & 6) != 0`；"谁托着它"用宿主公开 API `TowerDefenseCellInstance.GetSlot(character)`（:1534） |
  | 「连底座后能不能种」 | 就是同一个 `CanPacketPlant`（搬完底座再问一次），**不自己模拟** |
- ⚠️ **踩坑记录：不要自己"猜"目的地缺什么底座**。`CanPacketPlant` 里"花盆/莲叶让植物可种"的真正出口是
  `:1040 GetCharacterWhoHasGridType(packetConfig)` —— 只要格子里有一个"没被别人托着、`plantGridOverrideType`
  命中该植物 `plantGridType`"的角色就算能种（花盆 override=`POT`、莲叶 override=`LILYPAD`，而普通植物默认
  `plantGridType = {GROUND, POT, LILYPAD}`）。这和 `surroundRequireGroundOrPot`（:767 那段门，**默认 false**）无关。
  所以"先自己算一遍缺不缺底座"会漏掉最常见的**屋顶/砖地**场景（没有 `GROUND` 但有 `BRICK`，花盆能种、植物不能）
  ⇒ 本实现改成**直接试搬 + 宿主 `CanPacketPlant` 复核 + 失败回滚**，一步不猜。
- ⚠️ `IsSupport` / `IsSurround` / `BlocksSupport` 在 unpack 里都是 **`internal static`**
  （`unpack/addons/ModEditor/ModSystem/XWModPlantPlacementService.cs:257-299`）⇒ 反射调用；
  反射不到（宿主改版/裁剪）就用**逐字等价**的位运算兜底，日志会打一行提示，功能不会静默失效。
- **原子性（绝不出现"底座搬走了、植物还留在原地"）**：
  1. 搬底座（`EmitDestroy()` 让原格摘掉 + `sourceCell.MoveCharacterToCell(底座, targetCell, 0.01)`，
     与宿主 `PlaceCharacter` 逐字一致）；
  2. **真实**复核 `targetCell.CanPacketPlant(植物.packet)`；
  3. 通过 ⇒ 搬植物（同款 `EmitDestroy` + `MoveCharacterToCell`）+ 宿主同款收尾
     （`ShovelDig` 音效 → `feature.ClearPickedState()` → `packetPickControl.Release()`）；
  4. 不通过 / 抛异常 ⇒ `RollbackSupport` 把底座搬回原格（还是走同一个宿主 API）。
- **时机**：只在"确认点击"那一帧动手（`mapControl.IsConfirmInput()`，与宿主同一判据：
  PC = `Input.IsActionJustPressed("Press")`，手机 = `IsActionJustReleased`）。宿主处理输入在
  `_input` 阶段、本 Mod 挂在 `process_frame` ⇒ 同帧里我们一定看到"宿主已决定不放"之后的状态；
  置放成功后 `pickedCharacter` 被清空，下一帧自然不会重复。
- **"脚下底座"怎么找**：先查宿主自己的"谁托着谁"映射 `TowerDefenseCellInstance.characterSlotDictionary`
  （宿主里两个方向都写过：`CharacterPlant` 的 :358 / :390 ⇒ 两个方向都认），再用同格内所有
  `IsSupport` 为真的角色兜底 —— **不按名字猜**（花盆 / 莲叶 / 花盆莲叶 / 任何未来的支撑植物都自动命中）。
- **联机模式完全不接管**（宿主对底座没有搬家同步协议，硬搬会不同步）⇒ 保持原逻辑，日志打一次提示。
- 日志（受 `enableLog` 门控）：「连底座移动成功：<植物> + 底座 <底座> → (x,y)」/
  「连底座移动：底座能种，但植物本体仍放不下 ⇒ 回滚底座」/「目的地种不下，且这株植物脚下没有花盆 / 莲叶 ⇒ 按原逻辑不放」。

### 可放置位置提示：**不再由本 Mod 绘制**

v1.0.1 曾用 RenderingServer 自绘绿色半透明格 + 琥珀源格框，玩家反馈样式不对（要求与「种植位置标识」一致）
⇒ v1.0.2 **整体删除**（含 RID 画布/节流/合法性重算/`GlovePickHighlight*` 全部代码）。
现在本 Mod 不画任何格子；"哪些格子能放"由 `PlantPlacementMarker`（种植位置标识）用自己的样式负责。

## 面板关闭（v1.0.1 核查结论）

- 本 Mod **不直接引用**任何 `ModPosLayer_*` / `ModPosPanel_*` / `ModPosBtn_*` 节点，
  也不写它们的 `Visible`；每帧只调 `PosEditorRow.Ensure(...)`（幂等刷新行内容，不改可见性）。
- `PosEditorRow` 里唯一把面板设为可见的地方是 `TogglePanel()`（由入口按钮点击触发），
  `EnsurePanel()` 只在**新建**层时设 `Visible=false` ⇒ 不存在"关了又被打开"的路径。
- Lead 已把关闭改成三条路径：标题栏「关闭」（沿祖先链找 CanvasLayer，不依赖设置页是否还在）、
  全屏透明遮罩按钮（点面板外关闭）、入口按钮切换；关闭时会打印 `[PosEditorRow] 面板已关闭：glove（来源）`。

## 关键实现：为什么要 reparent

手套 UI（`GloveManager`，`Control`，70×72）被 `TowerDefenseBattleFeatureGlove.Init` 塞进
`control.AddUIToTopPropContainer(...)`，而 `uITopPropContainer` 是 **`HBoxContainer`**
（`BankUILayer/UITopContainer` 下）⇒ **容器托管布局，直接改 position 会被每帧覆盖**。

所以自定义位置时：

1. 在战斗的 `bankUILayer`（同一个 `CanvasLayer`，层级与原生完全一致）下新建一个
   **全屏 `Control`**（`ModGloveOverlay`，`MouseFilter = Ignore`）；
2. 把 `GloveManager` **搬进**这个 Control（手写 `RemoveChild` + `AddChild`，行为确定）；
3. 按百分比设置 `Position`：`pos = (视口 − 控件尺寸) × 百分比 / 100`；
4. `auto` 模式则把控件**搬回** `uITopPropContainer`，并还原 `Visible = true`。

⚠️ 搬动会触发 `GloveManager._ExitTree`（而重新进树**不会**再跑 `_Ready`），那里会：
- `Pressed -= …` / `Toggled -= …` 摘掉按钮回调 ⇒ 本 Mod 搬完立刻 `RehookButton()` 幂等重挂；
- `QueueFree()` 掉地图上的「手套影子」`mapGloveSprite` ⇒ 下一帧 `EnsureMapGloveSprite()` 兜底重建
  （按 `_Ready` 的原逻辑：同贴图、`Scale = 80 / 贴图宽`，挂到 `mapControl.spriteNode`）。

运行时还按 `uITopPropContainer.IsVisibleInTree()` 同步 `GloveManager.Visible`，
使「顶部栏被动画收起时手套也隐藏」与原生表现一致。

## 配置项（全部 immediate / affectsGameplay=false）

`enabled`、`landMode`+`landX`+`landY`（唯一一套位置）、`moveWithBase`（v1.0.6：连花盆/莲叶一起移动）、
`shovelWhiteMask`、`enableLog`、`aboutChangelog`、`aboutAuthor`。

## 构建 / 装机

```bash
python mods/_build_locked.py Glove --install
```

（`_build_locked.py` 负责串行加锁 —— 各 Mod 的 `build_and_install.py` 开头会 `taskkill dotnet.exe`，
并行跑会互相杀。）

产物：`dist/Glove.pmod` → 复制到 `%APPDATA%\Godot\app_userdata\植物大战僵尸杂交版\Mods\Glove.pmod`，
并删除该 Mod 的 `ModsCache/glove` 解包缓存。

## 共享组件（只读引用，一个字节都不许改）

`..\..\PosEditorRow.cs`、`..\..\BattleOptionColumns.cs`、`..\..\QrCodeBitmap.cs`、`..\..\SponsorQrInjector.cs`。

## 仍待实机验证

1. 普通关卡里手套按钮是否正常出现、点击可进入搬运模式（拿起 / 放下植物）。
2. 自定义位置（九宫格 / 百分比）在横屏与竖屏下是否都落点正确；窗口拉伸 / 手机旋转后是否跟随。
   （v1.0.5 起只有一套设置，横竖屏共用；面板里也不再有「竖版」那一行。）
3. reparent 后按钮点击、`gloveSprite` 图标切换、拖拽时「手套影子」跟随是否正常。
4. IZM / IZM2 关卡是否**确实没有**手套（且没有报错日志）。
5. 战斗设置页「手套 · 设置」面板**两行**（「位置」+「搬运 · 底座」）的即时生效与配置持久化
   （关掉面板重开、重启游戏后开关状态是否保持）。
6. 配置页「关于」栏收款码是否显示（`aboutAuthor` 首行以「手套」开头）。
7. **v1.0.2**：本 Mod 应**不再画任何格子**（拿起植物后战场上看不到绿色方块/源格框）；
   可放置位置提示应只剩「种植位置标识」Mod 自己那套样式（另一个队员在改，需与它一起验证）。
8. **v1.0.2 互斥**：手套选中时点铲子 ⇒ 手套应立刻取消（按钮弹起、地图手套影子消失）；
   铲子选中时点手套 ⇒ 铲子应立刻取消（铲子按钮弹起、地图铲子影子消失）；
   用快捷键（`Glove` / `Shovel`）交叉切换同样生效；任何时刻打开设置页/战斗中都不该出现两者同时选中。
   日志（开 enableLog 时）应出现「手套与铲子互斥：已取消铲子（手套刚被选中）」/「…已取消手套（铲子刚被选中）」。
9. **v1.0.1**：铲子悬停时整株是否变白、换株是否只白一株、离开/放下铲子后是否**完全还原**
   （重点看有没有"白着不恢复"）；关掉「铲子悬停白色遮罩」后是否一次都不写。
10. **v1.0.1**：位置面板的「关闭」/点面板外关闭是否生效（日志应出现
    `[PosEditorRow] 面板已关闭：glove（标题栏关闭）` / `（点击面板外）`），且不会被每帧扫描重新打开。
11. **v1.0.3 点击屏蔽**：把**玉米加农炮 / 毁灭加农炮 / 泡椒罐子**用手套搬到别的格子后，
    它们**不应**被"放下"的那一次点击触发（不开火、不引爆）；之后再正常点它们应照常响应。
    开 `enableLog` 时日志应出现「已临时屏蔽点击：<名字>（搬运期间 + 放置后 250ms）」与
    「手套放置：已临时屏蔽刚放下植物的点击…」，随后「已还原点击屏蔽（窗口结束）」。
12. **v1.0.3 预览外观**：搬**毁灭加农炮**时，跟随鼠标的预览应与本体外观**完全一致**（不是基础玉米加农炮）；
    分别在"拖到可放置格上（贴格）"与"拖到空地/界外（跟鼠标-20px）"两种状态各看一眼。
    若预览变成**空白/不显示**，日志里会有「真实外观预览：克隆的渲染管线没有初始化…已回退到宿主默认预览」
    ⇒ 说明 `AdobeAnimateSprite.Duplicate()` 这条路不可用，需要改用"让真实 sprite 跟随鼠标"的方案。
13. **v1.0.3 回归**：搬普通植物（豌豆/向日葵）时预览与点击都正常；连续多次搬同一株不应残留半透明克隆。
14. **v1.0.6 连底座移动（核心）**：找一个"植物本身在目的地种不下"的场景 —— 例如**水池关**里把
    「向日葵 / 豌豆」这类只能种在土上的植物，连同它脚下的**莲叶**一起往另一个水格搬；或**屋顶 / 无土格**
    上连同**花盆**搬。预期：底座 + 植物**两层都到目的地**，植物能正常攻击/生长，且不是"浮在空中"或"陷进地面"。
    开 `enableLog` 时日志应出现「连底座移动成功：<植物> + 底座 <底座> → (x,y)」。
15. **v1.0.6 关闭开关 = 旧行为**：面板里关掉「搬运 · 底座」（或配置页把 `moveWithBase` 关掉）后，
    同样的"种不下"操作应**完全不动**（底座也不动），日志出现「…按原逻辑不放」。
16. **v1.0.6 回滚不留脏**：目标格有别的阻塞（冰车痕 / 墓碑 / 界外一角等）时，底座**不应**被搬走 ——
    日志应出现「回滚底座」或「底座不符合目的地地形，跳过」，且原格里的底座与植物**位置与关系都不变**
    （重点看：植物是否还压在底座上、底座是否还能正常铲 / 是否还能再往上种东西）。
17. **v1.0.6 花盆莲叶两栖**：用「花盆莲叶」当底座往**水面**搬 —— 应成功（普通花盆往水面会被跳过，
    日志「底座不符合目的地地形，跳过」）；这是"符合目的情况"的关键回归点。
18. **v1.0.6 原手感零改动**：目的地本来就能种时，放置的 tween / 音效 / 拖影与 v1.0.5 **完全一致**
    （本 Mod 不介入）；联机模式**不接管**（日志「联机模式：不接管…」）。
19. **v1.0.6 暂停态不接管（修复）**：先用手套拿起一株带底座的植物 → 打开战斗设置页（游戏暂停）→
    在页面/面板上随便点几下 → 关掉页面回到战场：植物与底座**都必须还在原来那格**（以前会"被搬到鼠标下的格子"）。
20. **v1.0.6 落盘可自证**：每次在面板里改「位置」或「搬运 · 底座」，`godot.log` 都应出现
    `[Glove] 配置已落盘：<键>=<值>`（这条不受 `enableLog` 门控）；重启游戏后面板里的勾选与位置应保持。
    若出现 `[Glove] 配置保存失败：…`，说明是宿主保存管道拒绝（消息带原因），把这一行发给作者即可定位。

## 2026-11-01 复核后补强（v1.0.6 同版本）

| 问题（只读复核发现） | 严重度 | 处理 |
|---|---|---|
| `supportMoved` 只在 `MoveCharacterCell` 整步成功后才置位 ⇒ `EmitDestroy()` 抛 / `MoveCharacterToCell()` 后半段抛时 catch 不回滚，底座会"既不在原格也不在目标格"（坏档级） | 中 | 新增 `EnsureSupportBackInSource()`：按**实际成员关系**（`source.characterList.Contains(support)`）决定"只还原堆叠映射"还是"补搬回原格"，三种异常态都收 |
| 暂停态误触发：每帧钩子挂 `process_frame`（暂停照发），宿主 `ProcessInput` 暂停时不跑、`OptionButtonPressed()` 也不清 `pickedCharacter` ⇒ 拿着植物打开设置页点一下就可能真搬家 | 中 | `TickMoveWithBase()` 首行加 `if (tree.Paused) return;`（联机模式下 `BattleOption._Ready` 会关掉暂停，不挡正常用法） |
| 启动日志仍打 `v1.0.5 ready` | 低 | 改 `v1.0.6` |
| 面板标题仍叫「手套按钮 · 位置」（现在有两行） | 低 | 标题/入口统一成「手套 · 设置」（节点名仍由 `PanelSpec.Id="glove"` 决定，幂等复用不受影响） |
| 保存失败只有一行 Warn、成功无日志 ⇒ 玩家反馈"设置没了"时无法自证 | 低 | 成功也打 `[Glove] 配置已落盘：…`（不门控）；失败日志补一句"只在本次会话生效" |
