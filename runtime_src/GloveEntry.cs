using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「手套」Mod —— 托管运行时入口。
///
/// ── 需求（task-5 / 用户）────────────────────────────────────────────
/// 1. **启用手套**：在**非**「我是僵尸」(IZM)、**非**「我是僵尸大冒险」(IZM2) 的关卡里，
///    自动启用手套（游戏自带的"搬植物"手套）。IZM / IZM2 必须保持原样（不启用）。
/// 2. **配置**（mod.json settings，全部 immediate / affectsGameplay=false）：
///    enabled 总开关、landMode+landX+landY（**横版**用）、portMode+portX+portY（**竖版**用）、
///    enableLog、aboutChangelog、aboutAuthor。
/// 3. **横竖版判定 + 百分比定位**：视口宽 &gt; 高 = 横版，高 &gt;= 宽 = 竖版；
///    九宫格锚点（tl/tc/tr/ml/mc/mr/bl/bc/br）+ custom 百分比坐标 + auto（保持游戏原位置）。
/// 4. **战斗设置页**：Mod 按钮列（`ModCol4`）注入「手套·按钮位置」→ 打开**本 Mod 独占**浮层面板，
///    面板里两行「横版 · 位置」「竖版 · 位置」（共享组件 `PosEditorRow.PanelSpec`）。
/// 5. **收款码**：每帧 `SponsorQrInjector.Tick(tree, "手套", root, "Resources/SponsorQR.png")`。
///
/// ── ★ v1.0.1 新增（task-6 / 用户）─────────────────────────────────
/// 6. **铲子悬停植物整株白色遮罩**（用户原话「选择到对应的植物时整个植物加上白色遮罩」+ 开关）：
///    读 `ShovelManager.shovelPick` / `.lastShovelPlant`，对 `TowerDefensePlant` 每帧写
///    `SetSpriteGroupShaderParameter("whiteStrength", …)`（宿主亮度通道，见 `ShovelWhiteStrength`），
///    离开时写回 0；配置项 `shovelWhiteMask` 关掉后完全不再干预（并保证清零）。
///
/// ── ★ v1.0.2 变更（task-8 / 用户）─────────────────────────────────
/// 7. **删掉本 Mod 自绘的"可放置位置"格子**（玩家：样式不对，要和「种植位置标识」一样的样式）：
///    原先用 RenderingServer 画绿色半透明方块 + 琥珀源格框的实现（含 RID/画布/节流/合法性重算）
///    已**整体删除**，本 Mod 不再画任何格子 —— 可放置位置的显示交给 `PlantPlacementMarker`。
/// 8. **铲子 / 手套 互斥**（玩家：「为什么又能选铲子又能选手套？」）：宿主
///    `PacketPickControl.ProcessTools`（:1221-1251）只处理"选卡 vs 工具"，不管工具之间互斥
///    ⇒ 这里每帧做**上升沿互斥**：手套刚选中就取消铲子、铲子刚选中就取消手套（详见 `TickToolMutex`）。
///
/// ── 解包源码依据（全部实查，unpack/）────────────────────────────────
/// · 手套是战斗 Feature，注册名 `"Glove"`：`Registry/Battle/TowerDefenseBattleRegistry.cs:34`。
/// · 正常关卡**默认没有手套**：游戏自己只在调试开关下加
///   （`TowerDefenseControlNew.cs:487  if (CommandManager.Instance.debugOpenGlove) AddFeature("Glove", new Dictionary());`）。
/// · 开关入口：`TowerDefenseControlNew.AddFeature(StringName, Dictionary)`（public，同文件 :562）。
///   `Init` 阶段 `TowerDefenseBattleFeatureGlove.Init` 会
///   `control.AddUIToTopPropContainer(gloveManager)`（:30）——挂进 `uITopPropContainer`。
/// · `uITopPropContainer` 是 **HBoxContainer**（`TowerDefenseControlNew.cs:31/231`，
///   场景里在 `BankUILayer/UITopContainer` 下）⇒ **容器托管布局，改 position 会被容器覆盖**
///   ⇒ 自定义位置必须**把 `GloveManager` 控件 reparent 到自建的全屏 Control** 上再按百分比定位；
///   `auto` 模式则**原样不动**（保持游戏原位置）。
/// · IZM 判定：`TowerDefenseManager.IsIZMMode()`（:1397）/ `IsIZM2Mode()`（:1415）；
///   手套自己的快捷键回调也正好用这两个排除（`GloveManager.cs:75`）。
/// · 取管理器：`TowerDefenseManager.GetGloveFeature()`（:490）→ `feature.gloveManager`（可能为 null）。
///
/// ── 战斗中途 AddFeature 的补充动作（关键，别漏）────────────────────
/// 游戏只在**关卡初始化**时跑 `GameInit` / `GameStart` 生命周期
/// （`TowerDefenseControlNew.GameInitEntered` / `InvokeGameStartLifecycle`）。
/// 我们是在战斗**已经跑起来以后**才加 Feature 的 ⇒ 必须自己补这两步：
///   · `GameInit()`  → `InitializeManager()`：把 `mapFeature.gloveManager` 接上、`gloveManager.Init(mapControl, mapFeature)`；
///   · `GameStart()` → 创建 `GlovePickTool` 并 `packetPickControl.RegisterTool(...)`（否则手套点了没用）。
/// 两步都幂等（`GameStart` 有 `!IsInstanceValid(glovePickTool)` 守卫），即使与游戏自己的
/// 生命周期重叠也不会重复注册。`Process` 不用管 —— `GameRunningProcessing` 会遍历
/// `featureDictionary.Values` 逐个 `Process(delta)`。
///
/// ── 铁律 ───────────────────────────────────────────────────────────
/// · 入口三回调一律 try/catch（抛出 = 整包回滚，玩家看到"加载失败"）。
/// · 本程序集**没有 Godot 源码生成器** ⇒ 自定义 Node 子类的 `_Process` 不会被调用，
///   每帧逻辑一律挂 `SceneTree.process_frame` 信号（照抄 SpawnRate / TimeStop）。
/// · `Godot.Collections.Dictionary` 遍历要用 `KeyValuePair<Variant, Variant>`（本文件目前不需要遍历）。
/// </summary>
public sealed partial class GloveEntry : IXWModRuntimeEntry
{
	// ================================================================ 常量

	/// <summary>必须与 mod.json 的 `name` **逐字一致** —— 收款码注入器靠"关于富文本是否以本名字开头"判定归属。</summary>
	private const string ModName = "手套";

	private const string LogPrefix = "[Glove] ";

	// ── 配置键（与 mod.json 的 settings[].key 一一对应）──
	private const string KEnabled = "enabled";
	private const string KLandMode = "landMode";
	private const string KLandX = "landX";
	private const string KLandY = "landY";
	private const string KPortMode = "portMode";
	private const string KPortX = "portX";
	private const string KPortY = "portY";
	private const string KEnableLog = "enableLog";
	/// <summary>★ v1.0.1：铲子悬停植物时的整株白色遮罩开关。</summary>
	private const string KShovelWhiteMask = "shovelWhiteMask";

	/// <summary>手套 Feature 的注册名（`TowerDefenseBattleRegistry` 里的字符串）。</summary>
	private const string GloveFeatureName = "Glove";

	/// <summary>
	/// 刚放置那一株的额外屏蔽窗口（毫秒）。取 250ms —— 与宿主 `MousePressComponent`
	/// 自己的 `EmulatedMouseSuppressionMsec = 250` 同量级，够覆盖"同一次点击的后续输入"。
	/// </summary>
	private const uint CarryClickGuardMsec = 250u;

	/// <summary>手套模式期间"重扫全场植物"的间隔（毫秒）—— 新种下的植物也要屏蔽。</summary>
	private const uint ClickSuppressRescanMsec = 500u;

	/// <summary>自建全屏悬浮层（承载被 reparent 出来的手套控件）的节点名。</summary>
	private const string OverlayName = "ModGloveOverlay";

	/// <summary>扫描节流：每 N 帧做一次"加 Feature / 扫设置页"的较重逻辑（定位本身每帧都做）。</summary>
	private const int ScanStride = 6;

	/// <summary>
	/// 战斗启动后**再等几帧**才加手套 Feature —— 避免与游戏自己的
	/// `GameRunningEntered → InvokeGameStartLifecycle` 抢同一帧（虽然两边都幂等，
	/// 错开一点最稳）。6 帧 ≈ 0.1 秒，玩家无感。
	/// </summary>
	private const int RunGraceFrames = 6;

	/// <summary>本 Mod 独占的「按钮位置」面板（**不要**复用时停/加速的 spec id）。</summary>
	private static readonly PosEditorRow.PanelSpec PosSpec =
		new PosEditorRow.PanelSpec("glove", "手套按钮 · 位置", "手套·按钮位置");

	/// <summary>位置预设值（与 mod.json 里 landMode/portMode 的 options **逐字一致**）。</summary>
	private static readonly string[] PosPresetValues =
	{
		"auto", "tl", "tc", "tr", "ml", "mc", "mr", "bl", "bc", "br", "custom"
	};

	private static readonly string[] PosPresetLabels =
	{
		"自动（原位置）", "左上", "中上", "右上", "左中", "正中", "右中", "左下", "中下", "右下", "自定义"
	};

	/// <summary>哪个预设代表"自定义百分比坐标"。</summary>
	private const string PosCustomValue = "custom";

	/// <summary>手套控件在被 HBoxContainer 托管时的尺寸（场景里 `custom_minimum_size = Vector2(70, 72)`）。</summary>
	private const float GloveW = 70f;
	private const float GloveH = 72f;

	// ────────────────────────────────────────────── ★ v1.0.1 新增（搬运高亮 / 铲子白色遮罩）
	/// <summary>铲子 Feature 的注册名（`TowerDefenseBattleRegistry.RegisterInit` 里注册的字符串）。</summary>
	private const string ShovelFeatureName = "Shovel";

	/// <summary>
	/// 角色 shader 里"整株变白"的实例参数名。
	/// 依据：`unpack/Asset/Shader/TowerDefense/ShaderTowerDefenseCharacter.gdshader:9`
	///   `instance uniform float whiteStrength : hint_range(0.0, 1.0, 0.001) = 0.0 ;`
	///   以及 `:235  COLOR += whiteStrength ;` ⇒ 写 1.0 即整株纯白。
	/// </summary>
	private const string WhiteStrengthParam = "whiteStrength";

	// ================================================================ 运行状态

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _started;
	private bool _faultReported;
	private bool _optFaultReported;
	private bool _settingsSubscribed;
	private long _frame;

	/// <summary>诊断日志开关（配置项 enableLog；只走 `if (!开关) return;` 写法）。</summary>
	private bool _log;

	// ── 配置缓存（Settings.Changed 里刷新；每帧只读字段，不重复 Get）──
	private bool _enabled = true;
	private string _landMode = "auto";
	private int _landX = 84;
	private int _landY = 2;
	private string _portMode = "auto";
	private int _portX = 84;
	private int _portY = 2;
	/// <summary>★ v1.0.1：铲子悬停植物时的整株白色遮罩（默认开）。</summary>
	private bool _shovelWhiteMask = true;

	// ── v1.0.2：铲子 / 手套 互斥（宿主 `PacketPickControl.ProcessTools`（:1221-1251）只处理
	//    "选卡 vs 工具"，**不处理工具之间互斥** ⇒ 两个工具能同时处于 picking 状态，
	//    玩家实测"又能选铲子又能选手套"。这里做**上升沿互斥**：谁刚被选中，就立刻取消另一方。
	private bool _prevGlovePick;
	private bool _prevShovelPick;
	/// <summary>本次冲突已处理过（避免"稳态残局"每帧反复取消）。</summary>
	private bool _mutexConflictHandled;
	/// <summary>上一帧看到的工具实例 id：换关/重开时 Feature 会重建 ⇒ 用它复位沿状态。</summary>
	private ulong _mutexGloveId;
	private ulong _mutexShovelId;

	// ── v1.0.1：铲子白色遮罩的当前目标（离开时要写回 0）──
	private TowerDefenseCharacter _maskedChar;
	/// <summary>铲子遮罩异常只报一次。</summary>
	private bool _maskErrLogged;
	/// <summary>互斥处理异常只报一次。</summary>
	private bool _mutexErrLogged;
	/// <summary>放置后点击屏蔽异常只报一次。</summary>
	private bool _clickGuardErrLogged;
	/// <summary>真实外观预览异常只报一次。</summary>
	private bool _previewErrLogged;

	// ── v1.0.4：手套模式期间的"全场植物点击抑制"（v1.0.3 的"只屏蔽被搬那一株"必然晚一拍）──
	/// <summary>一条屏蔽记录：组件 + 原本是否活着 + 最早可还原时刻。</summary>
	private sealed class ClickSuppressionEntry
	{
		public MousePressComponent Comp;
		public bool WasAlive;
		public ulong UntilMsec;
		public string Name;
	}

	private readonly Dictionary<ulong, ClickSuppressionEntry> _clickSup
		= new Dictionary<ulong, ClickSuppressionEntry>();
	/// <summary>"取不到 MousePressComponent：<名字>" 每个名字只打一次。</summary>
	private readonly HashSet<string> _noMousePressWarned = new HashSet<string>(StringComparer.Ordinal);
	/// <summary>扫描用的临时缓冲（注册表返回的是内部复用 List，先复制再动它，避免边遍历边改）。</summary>
	private readonly List<TowerDefenseCharacter> _scanScratch = new List<TowerDefenseCharacter>(256);
	private bool _clickSupMode;
	private int _clickSupHoverTick;
	private ulong _clickSupRescanMsec;

	// 搬运跟踪（用于"拿起那一帧撤销误触发" + "放下后 250ms 窗口"）
	private ulong _undoPressDoneForId;
	private ulong _carriedId;
	private TowerDefenseCharacter _carriedChar;
	private Vector2I _carriedGrid;
	private bool _carriedActive;

	// ── v1.0.3：真实外观预览（克隆本体 sprite）──
	private AdobeAnimateSprite _previewClone;
	private ulong _previewCarryId;
	private bool _previewTried;
	private bool _previewHealthyChecked;
	/// <summary>本关卡内克隆不可用 ⇒ 回退宿主默认预览，不再重试。</summary>
	private bool _previewFallback;

	// ── 战斗状态 ──
	/// <summary>当前关卡的 key（`currentLevelConfig.name`），用于识别换关后复位一次性标志。</summary>
	private string _levelKey;
	/// <summary>`isGameRunning` 连续帧数（用于 RunGraceFrames 宽限）。</summary>
	private int _runFrames;
	/// <summary>AddFeature 失败只 Warn 一次（每次换关重置）。</summary>
	private bool _addWarned;
	/// <summary>总开关关闭时"尝试移除手套"只做一次（每次换关重置）。</summary>
	private bool _removeTried;
	/// <summary>自建悬浮层（被 reparent 的手套控件挂在这上面）。</summary>
	private Control _overlay;
	/// <summary>上一帧刚搬动过控件 ⇒ 下一帧检查地图手套影子是否被 `_ExitTree` 清掉（懒修复）。</summary>
	private bool _repairSpriteNext;
	/// <summary>本关卡的手套是不是**本 Mod 加的**（只有自己加的才在关闭总开关时移除，别动游戏自带的）。</summary>
	private bool _addedByUs;
	/// <summary>
	/// 手套控件**第一次被我们看到时的父节点**（= 游戏给它的「原位置」）。
	/// 不硬编码 `uITopPropContainer`：宿主在某些关卡会把顶部 UI 注册进 `TowerDefenseCoexistHud`
	/// （`AddUIToTopPropContainer` 里那条分支），那时原生父节点并不是 HBoxContainer。
	/// </summary>
	private Node _nativeParent;

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			ReloadSettings();
			Info("初始化完成；PackageRoot=" + ((context == null) ? "<null>" : context.PackageRoot)
				+ "；enabled=" + _enabled + "，横版=" + _landMode + "(" + _landX + "," + _landY + ")"
				+ "，竖版=" + _portMode + "(" + _portX + "," + _portY + ")。");
		}
		catch (Exception ex)
		{
			SafeWarn("Initialize 异常（已吞）：" + ex.Message);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				SafeWarn("拿不到 SceneTree，本 Mod 不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tickCallable = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tickCallable);
			_started = true;
			try
			{
				if (_context?.Settings != null && !_settingsSubscribed)
				{
					_context.Settings.Changed += OnSettingsChanged;
					_settingsSubscribed = true;
				}
			}
			catch { }
			// ★ 不受 enableLog 门控：每次启动只打 1 行，用来确认"包真的加载且入口跑起来了"。
			try
			{
				GD.Print(LogPrefix + "v1.0.4 ready（手套；enableLog=" + (_log ? "on" : "off") + "）");
			}
			catch { }
			Info("已挂载 process_frame（每 " + ScanStride + " 帧轮询一次「启用手套 / 设置页按钮」。");
		}
		catch (Exception ex)
		{
			SafeWarn("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			try
			{
				if (_context?.Settings != null && _settingsSubscribed) { _context.Settings.Changed -= OnSettingsChanged; }
			}
			catch { }
			_settingsSubscribed = false;
			// 卸载前尽量把控件搬回游戏原来的容器（搬不回去也无所谓，下面的 QueueFree 会连带回收）
			try { RestoreNativeParent(); } catch { }
			try
			{
				if (_overlay != null && GodotObject.IsInstanceValid(_overlay)) { _overlay.QueueFree(); }
			}
			catch { }
			_overlay = null;
			// ★ v1.0.1：卸载时立刻还原可能残留的白色遮罩（否则那株植物会一直白着）并释放高亮画布
			try
			{
				if (_maskedChar != null) { WriteWhite(_maskedChar, 0f); }
			}
			catch { }
			_maskedChar = null;
			// v1.0.4：卸载时无条件还原所有点击屏蔽 + 释放预览克隆
			try { RestoreAllSuppressedForce("卸载"); } catch { }
			try { ClearPreviewClone(); } catch { }
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tickCallable);
			}
		}
		catch (Exception ex)
		{
			SafeWarn("Shutdown 异常（已吞）：" + ex.Message);
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 配置读取

	private void ReloadSettings()
	{
		try
		{
			_enabled = ReadBool(KEnabled, true);
			_landMode = NormalizeMode(ReadString(KLandMode, "auto"));
			_landX = ReadInt(KLandX, 84, 0, 100);
			_landY = ReadInt(KLandY, 2, 0, 100);
			_portMode = NormalizeMode(ReadString(KPortMode, "auto"));
			_portX = ReadInt(KPortX, 84, 0, 100);
			_portY = ReadInt(KPortY, 2, 0, 100);
			_shovelWhiteMask = ReadBool(KShovelWhiteMask, true);
			_log = ReadBool(KEnableLog, false);
		}
		catch { }
	}

	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try
		{
			ReloadSettings();
			// 总开关被重新打开 ⇒ 允许再试一次"移除"（否则本关卡内会一直跳过）
			if (_enabled) { _removeTried = false; }
			// v1.0.1：配置改动时打一行（便于实机确认新开关真的被读到；受 enableLog 门控）
			Info("配置已刷新：铲子悬停白色遮罩=" + (_shovelWhiteMask ? "开" : "关")
				+ "，启用手套=" + (_enabled ? "开" : "关"));
		}
		catch { }
	}

	/// <summary>读 bool（兼容 bool / "true" / JsonElement；取不到用默认值）。</summary>
	private bool ReadBool(string key, bool def)
	{
		try
		{
			if (_context?.Settings == null) { return def; }
			object v = _context.Settings.Get<object>(key);
			if (v is bool b) { return b; }
			if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
		}
		catch { }
		return def;
	}

	/// <summary>读 int（宿主把 int 型配置规范化成 int；兼容 double / JsonElement）并钳到 [min,max]。</summary>
	private int ReadInt(string key, int def, int min, int max)
	{
		int v = def;
		try
		{
			if (_context?.Settings == null) { return def; }
			object raw = _context.Settings.Get<object>(key);
			if (raw is int i) { v = i; }
			else if (raw is long l) { v = (int)l; }
			else if (raw is double d) { v = (int)Math.Round(d); }
			else if (raw is float f) { v = (int)Math.Round(f); }
			else if (raw is string s && int.TryParse(s, out int si)) { v = si; }
			else if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Number)
			{
				v = je.TryGetInt32(out int ji) ? ji : (int)Math.Round(je.GetDouble());
			}
		}
		catch { }
		return Mathf.Clamp(v, min, max);
	}

	/// <summary>读 string（兼容 string / JsonElement）。</summary>
	private string ReadString(string key, string def)
	{
		try
		{
			if (_context?.Settings == null) { return def; }
			object v = _context.Settings.Get<object>(key);
			if (v is string s) { return s; }
			if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				return je.GetString();
			}
		}
		catch { }
		return def;
	}

	/// <summary>写配置（宿主保存管道；失败只警告，不打断操作）。</summary>
	private void SaveSettings(Dictionary<string, object> changes)
	{
		try
		{
			if (_context?.Settings == null || changes == null || changes.Count == 0) { return; }
			if (!_context.Settings.TrySave(changes, out XWModSettingsSaveResult result))
			{
				SafeWarn("配置保存失败：" + ((result == null) ? "(无返回信息)" : result.Message));
				return;
			}
			if (result != null && result.RestartRequired)
			{
				Info("配置已保存（宿主提示需重启生效）。");
			}
		}
		catch (Exception ex)
		{
			SafeWarn("配置保存异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>把任意外来字符串收敛成配置里声明的 11 个预设值之一。</summary>
	private static string NormalizeMode(string mode)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(mode)) { return "auto"; }
			string m = mode.Trim();
			for (int i = 0; i < PosPresetValues.Length; i++)
			{
				if (string.Equals(PosPresetValues[i], m, StringComparison.Ordinal)) { return m; }
			}
		}
		catch { }
		return "auto";
	}

	// ================================================================ 每帧

	private void OnFrame()
	{
		// ★ 配置页「关于」栏收款码（共享组件；内部 30 帧节流 + 插好后停止全树查找）。
		//   modName 必须与 mod.json 的 name 逐字一致 —— 注入器靠"该富文本是否以本 Mod 名开头"判定归属。
		try
		{
			SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, ModName,
				_context?.PackageRoot ?? "", "Resources/SponsorQR.png");
		}
		catch { }

		_frame++;
		bool poll = ((_frame % ScanStride) == 0);

		try
		{
			MaintainGlove(poll);
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				SafeWarn("手套维护异常（本条只报一次）：" + ex.Message);
			}
		}

		// ── ★ 每帧逻辑（必须放在下面的 poll 早退之前）──
		//   ① 铲子白色遮罩：HitFlashComponent 也会写 whiteStrength，必须每帧维持，否则被覆盖；
		//   ② 铲子 / 手套 互斥：宿主不管工具之间互斥，这里做上升沿处理。
		try { TickShovelMask(); }
		catch (Exception ex)
		{
			if (!_maskErrLogged)
			{
				_maskErrLogged = true;
				SafeWarn("铲子白色遮罩异常（本条只报一次）：" + ex.Message);
			}
		}
		try { TickToolMutex(); }
		catch (Exception ex)
		{
			if (!_mutexErrLogged)
			{
				_mutexErrLogged = true;
				SafeWarn("铲子/手套互斥异常（本条只报一次）：" + ex.Message);
			}
		}
		// ★ v1.0.3：③ 搬运/放置期间屏蔽被搬植物的点击（加农炮/泡椒罐之类）；
		//   ④ 预览改用本体 sprite 的克隆（变体植物外观才一致）。
		try { TickClickSuppression(); }
		catch (Exception ex)
		{
			if (!_clickGuardErrLogged)
			{
				_clickGuardErrLogged = true;
				SafeWarn("植物点击抑制异常（本条只报一次）：" + ex.Message);
			}
		}
		try { TickRealPreview(); }
		catch (Exception ex)
		{
			if (!_previewErrLogged)
			{
				_previewErrLogged = true;
				SafeWarn("真实外观预览异常（本条只报一次）：" + ex.Message);
			}
		}

		if (!poll) { return; }
		try
		{
			EnsureOptionRows();
		}
		catch (Exception ex)
		{
			if (!_optFaultReported)
			{
				_optFaultReported = true;
				SafeWarn("战斗设置页注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ 启用手套 / 定位

	/// <summary>
	/// 维持"手套已启用 + 位置正确"。
	/// `poll == false` 的帧只做**定位**（便宜），加 Feature / 换关判定等重逻辑只在 poll 帧做。
	/// </summary>
	private void MaintainGlove(bool poll)
	{
		TowerDefenseManager mgr = null;
		try { mgr = TowerDefenseManager.Instance; } catch { }
		if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { ResetLevelState(); return; }

		TowerDefenseControlNew ctrl = null;
		try { ctrl = mgr.currentControl; } catch { }
		if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { ResetLevelState(); return; }

		// ── 换关 / 重开同一关：复位一次性标志（新关卡 = 新的 Feature 与控件）──
		string key = CurrentLevelKey(mgr);
		if (!string.Equals(key, _levelKey, StringComparison.Ordinal))
		{
			_levelKey = key;
			_addWarned = false;
			_removeTried = false;
			_runFrames = 0;
			_repairSpriteNext = false;
			_addedByUs = false;
			_nativeParent = null;
			// 悬浮层**不清空**：旧战斗的节点会随场景一起销毁（`IsInstanceValid` 会失配），
			// 若控件存活（同一战斗内重开）则 `EnsureOverlay` 会复用它，不会凭空多建一层。
		}

		// ── 总开关关闭 ──
		if (!_enabled)
		{
			_runFrames = 0;
			if (poll && !_removeTried)
			{
				_removeTried = true;
				TryRemoveGlove(ctrl, mgr);
			}
			return;
		}

		// 战斗是否真的跑起来了（关卡还没进 = 什么都不做）
		bool running = false;
		try { running = ctrl.isGameRunning; } catch { }
		if (!running) { _runFrames = 0; return; }
		_runFrames = Math.Min(_runFrames + 1, 100000);
		if (_runFrames < RunGraceFrames) { return; }

		// 没有关卡配置 = 还没加载完（`currentLevelConfig` 是 TowerDefenseManager 上的公开字段）
		TowerDefenseLevelBaseConfig levelCfg = null;
		try { levelCfg = mgr.currentLevelConfig; } catch { }
		if (levelCfg == null || !GodotObject.IsInstanceValid(levelCfg)) { return; }

		// ★★ IZM / IZM2 关卡**必须保持原样**（不启用手套）——
		//    与游戏自己 `GloveManager._Input` 的排除条件逐字一致（`GloveManager.cs:75`）。
		if (IsIzMMode(mgr)) { return; }

		TowerDefenseBattleFeatureGlove feature = null;
		try { feature = mgr.GetGloveFeature(); } catch { }
		if (feature == null || !GodotObject.IsInstanceValid(feature))
		{
			if (!poll) { return; }
			TryAddGlove(ctrl, mgr);
			try { feature = mgr.GetGloveFeature(); } catch { }
			if (feature == null || !GodotObject.IsInstanceValid(feature)) { return; }
		}

		ApplyPosition(ctrl, feature);
	}

	private void ResetLevelState()
	{
		_levelKey = null;
		_runFrames = 0;
		_addWarned = false;
		_removeTried = false;
		_repairSpriteNext = false;
		_addedByUs = false;
		_nativeParent = null;
		ResetMutexEdges();
		// v1.0.4：换关/退出时把"点击屏蔽"与"预览克隆"一起复位（避免上一关的引用串到下一关）
		try { RestoreAllSuppressedForce("换关/离开战斗"); } catch { }
		try { ClearPreviewClone(); } catch { }
		_previewCarryId = 0UL;
		_previewTried = false;
		_previewHealthyChecked = false;
		_previewFallback = false;
	}

	private static string CurrentLevelKey(TowerDefenseManager mgr)
	{
		try
		{
			TowerDefenseLevelBaseConfig cfg = mgr.currentLevelConfig;
			if (cfg == null || !GodotObject.IsInstanceValid(cfg)) { return null; }
			return cfg.name;
		}
		catch { return null; }
	}

	/// <summary>IZM（含 QUIZ，宿主自己就这么归）或 IZM2 ⇒ true ⇒ 本 Mod 不启用手套。</summary>
	private static bool IsIzMMode(TowerDefenseManager mgr)
	{
		try
		{
			if (mgr.IsIZMMode()) { return true; }
		}
		catch { }
		try
		{
			if (mgr.IsIZM2Mode()) { return true; }
		}
		catch { }
		return false;
	}

	/// <summary>
	/// 加手套 Feature，并**补跑**游戏在关卡初始化阶段才会跑的两步生命周期。
	/// 失败只 Warn 一次（每次换关重置），绝不每帧刷日志。
	/// </summary>
	private void TryAddGlove(TowerDefenseControlNew ctrl, TowerDefenseManager mgr)
	{
		bool ok = false;
		try
		{
			ok = ctrl.AddFeature(new StringName(GloveFeatureName), new Godot.Collections.Dictionary());
		}
		catch (Exception ex)
		{
			if (!_addWarned)
			{
				_addWarned = true;
				SafeWarn("启用手套异常（只报一次，已吞）：" + ex.Message);
			}
			return;
		}
		if (!ok)
		{
			if (!_addWarned)
			{
				_addWarned = true;
				SafeWarn("启用手套失败（只报一次）：AddFeature(\"Glove\") 返回 false，详情见上方 [AddFeature] 日志。");
			}
			return;
		}
		Info("已在本关卡启用手套（AddFeature(\"Glove\") 成功）。");
		_addedByUs = true;

		TowerDefenseBattleFeatureGlove feature = null;
		try { feature = mgr.GetGloveFeature(); } catch { }
		if (feature == null || !GodotObject.IsInstanceValid(feature)) { return; }

		// ★ 战斗中途加入 ⇒ 游戏不会替我们跑 GameInit / GameStart，必须自己补（两步都幂等）。
		try { feature.GameInit(); }
		catch (Exception ex) { SafeWarn("手套 GameInit 异常（已吞）：" + ex.Message); }
		try { feature.GameStart(); }
		catch (Exception ex) { SafeWarn("手套 GameStart 异常（已吞）：" + ex.Message); }
		Info("已补跑手套的 GameInit / GameStart（把 GlovePickTool 注册进 PacketPick）。");
	}

	/// <summary>总开关关闭时把手套从本关卡摘掉（`RemoveFeature` 有宿主侧依赖校验，失败也不要紧）。</summary>
	private void TryRemoveGlove(TowerDefenseControlNew ctrl, TowerDefenseManager mgr)
	{
		try
		{
			// 只移除**本 Mod 加的**手套；游戏自己带的手套（调试开关）不归我们管。
			if (!_addedByUs) { return; }
			TowerDefenseBattleFeatureGlove feature = mgr.GetGloveFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature)) { _addedByUs = false; return; }
			bool ok = ctrl.RemoveFeature(new StringName(GloveFeatureName));
			if (ok) { _addedByUs = false; }
			Info("总开关已关闭 ⇒ 移除本关卡的手套（RemoveFeature=" + (ok ? "true" : "false") + "）。");
		}
		catch (Exception ex)
		{
			SafeWarn("移除手套异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>
	/// 按"横/竖版 + 预设"把手套控件定位。
	/// · `auto`：**原样不动**（保持游戏原位置）；若之前被我们搬走过，搬回原容器。
	/// · 其余：把 `GloveManager` reparent 到**自建全屏 Control**（脱离 HBoxContainer 托管布局），
	///   再按百分比（0–100）定位 —— 九宫格锚点等价于固定的 (0/50/100, 0/50/100)。
	/// </summary>
	private void ApplyPosition(TowerDefenseControlNew ctrl, TowerDefenseBattleFeatureGlove feature)
	{
		GloveManager gm = null;
		try { gm = feature.gloveManager; } catch { }
		if (gm == null || !GodotObject.IsInstanceValid(gm)) { return; }

		// 上一帧刚搬动过 ⇒ 检查"地图上的手套影子"是否被 `GloveManager._ExitTree` 清掉了（懒修复）
		if (_repairSpriteNext)
		{
			_repairSpriteNext = false;
			EnsureMapGloveSprite(gm);
		}

		Node nativeParent = null;
		try
		{
			// 第一次见到这个控件 ⇒ 记下游戏给它的父节点（后面"auto 模式"就搬回这里）
			if (_nativeParent == null || !GodotObject.IsInstanceValid(_nativeParent)
				|| _nativeParent == _overlay)
			{
				Node p = gm.GetParent();
				if (p != null && GodotObject.IsInstanceValid(p) && p != _overlay)
				{
					_nativeParent = p;
				}
			}
			nativeParent = _nativeParent;
			if (nativeParent == null || !GodotObject.IsInstanceValid(nativeParent))
			{
				nativeParent = ctrl.uITopPropContainer;
			}
		}
		catch { }
		if (nativeParent != null && !GodotObject.IsInstanceValid(nativeParent)) { nativeParent = null; }
		if (nativeParent == _overlay) { nativeParent = null; }

		bool portrait = IsPortrait(ctrl);
		string mode = NormalizeMode(portrait ? _portMode : _landMode);
		int px = Mathf.Clamp(portrait ? _portX : _landX, 0, 100);
		int py = Mathf.Clamp(portrait ? _portY : _landY, 0, 100);

		if (string.Equals(mode, "auto", StringComparison.Ordinal))
		{
			// 保持游戏原位置：先把我们搬出来的控件搬回游戏自己的容器（没搬过就什么都不做）
			if (gm.GetParent() != nativeParent && (nativeParent != null || gm.GetParent() == _overlay))
			{
				Node back = nativeParent;
				if (back != null && MoveChild(gm, back))
				{
					_repairSpriteNext = true;
					RehookButton(gm);
					Info("位置模式 auto ⇒ 手套已搬回游戏原始容器（恢复原生位置）。");
				}
			}
			// ★ 自定义模式下我们会按"顶部栏是否可见"手动改 `Visible`；
			//   搬回原生容器后必须还原成 true，否则手套可能一直隐形（容器不再替我们管这个字段）。
			try
			{
				if (gm.GetParent() == nativeParent && !gm.Visible) { gm.Visible = true; }
			}
			catch { }
			return;
		}

		Control host = EnsureOverlay(ctrl);
		if (host == null) { return; }
		if (gm.GetParent() != host)
		{
			if (!MoveChild(gm, host)) { return; }
			_repairSpriteNext = true;
			RehookButton(gm);
			Info("已把手套控件 reparent 到自建全屏 Control（" + OverlayName
				+ "），不再受 HBoxContainer 托管布局约束。");
		}

		Vector2 vp = new Vector2(1333f, 600f);
		try
		{
			Vector2 r = host.GetViewport().GetVisibleRect().Size;
			if (r.X > 100f && r.Y > 100f) { vp = r; }
		}
		catch { }

		// 控件尺寸（被容器托管时可能是 70×80）；拿不到就用场景里的最小尺寸
		Vector2 sz = new Vector2(GloveW, GloveH);
		try
		{
			Vector2 cur = gm.Size;
			if (cur.X > 1f) { sz.X = cur.X; }
			if (cur.Y > 1f) { sz.Y = cur.Y; }
		}
		catch { }

		float fx, fy;
		PresetAnchor(mode, px, py, out fx, out fy);
		// 百分比按"可用空间"算（屏宽 − 控件宽），拉满 100% 也不会跑出屏幕
		float availX = Math.Max(0f, vp.X - sz.X);
		float availY = Math.Max(0f, vp.Y - sz.Y);
		Vector2 target = new Vector2(availX * fx / 100f, availY * fy / 100f);

		try
		{
			if (gm.Size.X < 1f || gm.Size.Y < 1f) { gm.Size = sz; }
			Vector2 cur = gm.Position;
			if (Math.Abs(cur.X - target.X) > 0.5f || Math.Abs(cur.Y - target.Y) > 0.5f)
			{
				gm.Position = target;
			}
			// 与原生表现一致：顶部栏（UITopContainer）被动画收起时，手套也跟着不显示
			// （`IsVisibleInTree()` 在 CanvasItem 上，不在 Node 上）
			bool show = true;
			if (nativeParent != null && nativeParent is CanvasItem nativeItem
				&& GodotObject.IsInstanceValid(nativeItem))
			{
				show = nativeItem.IsVisibleInTree();
			}
			if (gm.Visible != show) { gm.Visible = show; }
		}
		catch { }
	}

	/// <summary>预设 → 百分比锚点（custom 用滑块给的 X/Y）。</summary>
	private static void PresetAnchor(string mode, int customX, int customY, out float fx, out float fy)
	{
		fx = Mathf.Clamp(customX, 0, 100);
		fy = Mathf.Clamp(customY, 0, 100);
		switch (mode)
		{
			case "tl": fx = 0f; fy = 0f; break;
			case "tc": fx = 50f; fy = 0f; break;
			case "tr": fx = 100f; fy = 0f; break;
			case "ml": fx = 0f; fy = 50f; break;
			case "mc": fx = 50f; fy = 50f; break;
			case "mr": fx = 100f; fy = 50f; break;
			case "bl": fx = 0f; fy = 100f; break;
			case "bc": fx = 50f; fy = 100f; break;
			case "br": fx = 100f; fy = 100f; break;
			default: break;   // custom / 未知值：用滑块坐标
		}
	}

	/// <summary>横竖版判定：视口宽 &gt; 高 = 横版；高 &gt;= 宽 = 竖版（与需求逐字一致）。</summary>
	private bool IsPortrait(Node anyNode)
	{
		try
		{
			Viewport vp = null;
			if (anyNode != null && GodotObject.IsInstanceValid(anyNode)) { vp = anyNode.GetViewport(); }
			if (vp == null && _tree != null && GodotObject.IsInstanceValid(_tree) && _tree.Root != null)
			{
				vp = _tree.Root.GetViewport();
			}
			if (vp != null)
			{
				Vector2 s = vp.GetVisibleRect().Size;
				if (s.X > 0f && s.Y > 0f) { return s.Y >= s.X; }
			}
		}
		catch { }
		return false;
	}

	/// <summary>
	/// 取/建**本 Mod 专属**的全屏悬浮层（`Control`，挂在战斗的 `bankUILayer` 下 ⇒
	/// 与银行/种子栏同一个 CanvasLayer，层级完全一致；`MouseFilter=Ignore` 不挡战场点击）。
	/// 换关后旧的层随旧战斗一起销毁 ⇒ 按"父节点是否还是当前 bankUILayer"判重建。
	/// </summary>
	private Control EnsureOverlay(TowerDefenseControlNew ctrl)
	{
		try
		{
			if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { return null; }
			Node want = null;
			try { want = ctrl.bankUILayer; } catch { }
			if (want == null || !GodotObject.IsInstanceValid(want)) { want = ctrl; }

			if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
			{
				if (_overlay.GetParent() == want) { return _overlay; }
				try { _overlay.QueueFree(); } catch { }
				_overlay = null;
			}

			Control host = new Control();
			host.Name = OverlayName;
			host.MouseFilter = Control.MouseFilterEnum.Ignore;   // 空白处不挡战场点击
			host.SetAnchorsPreset(Control.LayoutPreset.FullRect);
			want.AddChild(host, false, Node.InternalMode.Disabled);
			_overlay = host;
			Info("已建立手套悬浮层（父节点=" + want.Name + "）。");
			return host;
		}
		catch (Exception ex)
		{
			SafeWarn("创建手套悬浮层异常（已吞）：" + ex.Message);
			return null;
		}
	}

	/// <summary>
	/// 把控件从当前父节点搬到新父节点（**手工 RemoveChild + AddChild**，行为确定）。
	/// ⚠️ 这一步会触发 `GloveManager._ExitTree`（它在那里会摘掉按钮回调、QueueFree 掉地图手套影子），
	/// 所以搬完必须 `RehookButton` + 下一帧 `EnsureMapGloveSprite` 补齐。
	/// </summary>
	private bool MoveChild(Node child, Node newParent)
	{
		try
		{
			if (child == null || newParent == null) { return false; }
			if (!GodotObject.IsInstanceValid(child) || !GodotObject.IsInstanceValid(newParent)) { return false; }
			if (child.GetParent() == newParent) { return true; }
			Node old = child.GetParent();
			if (old != null && GodotObject.IsInstanceValid(old)) { old.RemoveChild(child); }
			newParent.AddChild(child, false, Node.InternalMode.Disabled);
			return true;
		}
		catch (Exception ex)
		{
			SafeWarn("搬动手套控件异常（已吞）：" + ex.Message);
			return false;
		}
	}

	/// <summary>卸载时把控件搬回游戏原容器（搬不动就算了）。</summary>
	private void RestoreNativeParent()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return; }
			TowerDefenseControlNew ctrl = mgr.currentControl;
			if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { return; }
			TowerDefenseBattleFeatureGlove feature = mgr.GetGloveFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature)) { return; }
			GloveManager gm = feature.gloveManager;
			if (gm == null || !GodotObject.IsInstanceValid(gm)) { return; }
			Node nativeParent = _nativeParent;
			if (nativeParent == null || !GodotObject.IsInstanceValid(nativeParent) || nativeParent == _overlay)
			{
				nativeParent = ctrl.uITopPropContainer;
			}
			if (nativeParent == null || !GodotObject.IsInstanceValid(nativeParent)) { return; }
			if (gm.GetParent() != nativeParent) { MoveChild(gm, nativeParent); }
		}
		catch { }
	}

	/// <summary>
	/// ★ 手套按钮回调重挂（幂等：先摘再加，保证**恰好一次**）。
	/// 为什么必须做：`GloveManager._ExitTree` 会 `Pressed -= …` / `Toggled -= …`，
	/// 而 reparent 之后再进树**不会**重新 `_Ready` ⇒ 不重挂的话按钮就"点不动"了。
	/// </summary>
	private void RehookButton(GloveManager gm)
	{
		try
		{
			if (gm == null || !GodotObject.IsInstanceValid(gm)) { return; }
			TextureButton btn = gm.gloveButton;
			if (btn == null || !GodotObject.IsInstanceValid(btn)) { return; }
			try { btn.Pressed -= gm.GloveButtonPressed; } catch { }
			try { btn.Pressed += gm.GloveButtonPressed; } catch { }
			try { btn.Toggled -= OnGloveButtonToggledHook; } catch { }
			try { btn.Toggled += OnGloveButtonToggledHook; } catch { }
			Info("已重挂手套按钮回调（防 reparent 后按钮失效）。");
		}
		catch { }
	}

	/// <summary>按钮 toggle 状态变化 ⇒ 刷新图标显示（原 `GloveManager.OnGloveButtonToggled` 是私有的）。</summary>
	private void OnGloveButtonToggledHook(bool _pressed)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return; }
			TowerDefenseBattleFeatureGlove feature = mgr.GetGloveFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature)) { return; }
			GloveManager gm = feature.gloveManager;
			if (gm != null && GodotObject.IsInstanceValid(gm)) { gm.UpdateDisplay(); }
		}
		catch { }
	}

	/// <summary>
	/// 兜底修复"地图上的手套影子"（`GloveManager.mapGloveSprite`）。
	/// `GloveManager._ExitTree`（reparent 时会被触发）会 `QueueFree` 它，
	/// 而重新进树不跑 `_Ready` ⇒ 不补的话手套拖拽时**没有跟随鼠标的手套图标**。
	/// 这里按 `_Ready` 的原逻辑重建一个并回填到公开字段上。
	/// </summary>
	private void EnsureMapGloveSprite(GloveManager gm)
	{
		try
		{
			if (gm == null || !GodotObject.IsInstanceValid(gm)) { return; }
			if (GodotObject.IsInstanceValid(gm.mapGloveSprite)) { return; }

			Sprite2D sp = new Sprite2D();
			sp.Name = "ModGloveMapSprite";
			sp.Visible = false;
			try
			{
				if (GodotObject.IsInstanceValid(gm.gloveSprite))
				{
					Texture2D tex = gm.gloveSprite.Texture;
					if (tex != null && GodotObject.IsInstanceValid(tex))
					{
						sp.Texture = tex;
						float w = tex.GetWidth();
						if (w > 0f) { sp.Scale = Vector2.One * (80f / w); }
					}
				}
			}
			catch { }

			Node2D mount = null;
			try
			{
				TowerDefenseBattleFeatureMap mapFeature = gm.mapFeature;
				if (mapFeature == null || !GodotObject.IsInstanceValid(mapFeature))
				{
					mapFeature = TowerDefenseManager.GetMapFeature();
				}
				if (mapFeature != null && GodotObject.IsInstanceValid(mapFeature)
					&& mapFeature.mapControl != null && GodotObject.IsInstanceValid(mapFeature.mapControl))
				{
					mount = mapFeature.mapControl.spriteNode;
				}
			}
			catch { }
			if (mount != null && GodotObject.IsInstanceValid(mount))
			{
				mount.AddChild(sp, false, Node.InternalMode.Disabled);
			}
			gm.mapGloveSprite = sp;
			Info("已重建地图手套影子（mapGloveSprite）。");
		}
		catch (Exception ex)
		{
			SafeWarn("重建地图手套影子异常（已吞）：" + ex.Message);
		}
	}

	// ================================================================ 铲子悬停白色遮罩

	/// <summary>
	/// ★ v1.0.1（用户需求「铲除植物时选中到对应植物 ⇒ 整个植物加白色遮罩 + 配置开关」）：
	/// 走宿主**现成通道** —— 角色 shader 的实例参数 `whiteStrength`（`COLOR += whiteStrength`），
	/// 写 1.0 即整株纯白；离开时写回 0。**不自己叠 Modulate**（会连带影响血条/阴影等其它绘制）。
	///
	/// ── 为什么必须"每帧维持" ─────────────────────────────────────
	/// `HitFlashComponent`（`unpack/Script/Component/TowerDefense/Character/HitFlashComponent/HitFlashComponent.cs:443`）
	/// 在受击闪白的推进/结束时会经 `SetHitFlashStrengths(...)` 把 **它自己那份** `_whiteStrength`
	/// 写进同一个 shader 参数 ⇒ 我们的 1.0 会被覆盖回 0。
	/// 所以只要还悬停着，就每帧写一次；离开/关开关时写回 0（只写一次，之后不再碰）。
	///
	/// ── "铲子选中了哪株植物"的判据（公开 API 优先）────────────────
	///   ① 首选 `ShovelManager.lastShovelPlant`：`ProcessShovelPick(...)` 里它就是
	///      "当前格子上被铲子选中的角色"（游戏自己还用 0.5 的 `toolHighlightStrength` 给它提亮）；
	///      `shovelPick == false` 或它为空 ⇒ 没在选中状态。
	///   ② 兜底：自己按 `ProcessShovelPick` 的同一套公式重算（见 `ResolveShovelHoverByMouse`）。
	///   只对 **`TowerDefensePlant`** 生效（墓碑/坑/护盾等其它可铲对象不加白）。
	/// </summary>
	private void TickShovelMask()
	{
		TowerDefenseCharacter prev = _maskedChar;
		TowerDefenseCharacter want = null;
		try
		{
			if (_shovelWhiteMask) { want = ResolveShovelMaskTarget(); }
		}
		catch { want = null; }

		// 每帧维持（见上面的"为什么必须每帧维持"）
		if (want != null) { WriteWhite(want, ShovelWhiteStrength); }

		// 换目标 / 离开 / 关掉开关 ⇒ 把上一株写回 0
		// （用 GetInstanceId 比较：Godot 对象的 C# 包装引用不一定同一份，比 id 最稳；
		//   上一株可能已被铲掉/销毁 ⇒ 先 IsInstanceValid 再取 id，避免 ObjectDisposedException）
		bool same = false;
		if (prev != null && want != null
			&& GodotObject.IsInstanceValid(prev) && GodotObject.IsInstanceValid(want))
		{
			try { same = prev.GetInstanceId() == want.GetInstanceId(); } catch { same = false; }
		}
		if (prev != null && !same)
		{
			WriteWhite(prev, 0f);
			_maskedChar = null;
		}
		if (want != null) { _maskedChar = want; }
	}

	/// <summary>当前应当整体变白的那株植物；没有就返回 null。</summary>
	private TowerDefenseCharacter ResolveShovelMaskTarget()
	{
		TowerDefenseManager mgr = null;
		try { mgr = TowerDefenseManager.Instance; } catch { }
		if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return null; }

		TowerDefenseControlNew ctrl = null;
		try { ctrl = mgr.currentControl; } catch { }
		if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { return null; }
		bool running = false;
		try { running = ctrl.isGameRunning; } catch { }
		if (!running) { return null; }

		TowerDefenseBattleFeatureShovel shovelFeature = null;
		try
		{
			shovelFeature = ctrl.GetFeature(new StringName(ShovelFeatureName)) as TowerDefenseBattleFeatureShovel;
		}
		catch { }
		if (shovelFeature == null || !GodotObject.IsInstanceValid(shovelFeature)) { return null; }

		ShovelManager shovelManager = null;
		try { shovelManager = shovelFeature.shovelManager; } catch { }
		if (shovelManager == null || !GodotObject.IsInstanceValid(shovelManager)) { return null; }

		bool picking = false;
		try { picking = shovelManager.shovelPick; } catch { }
		if (!picking) { return null; }

		TowerDefenseCharacter target = null;
		try { target = shovelManager.lastShovelPlant; } catch { }
		if (target == null || !GodotObject.IsInstanceValid(target))
		{
			try { target = ResolveShovelHoverByMouse(mgr); } catch { target = null; }
		}
		if (target == null || !GodotObject.IsInstanceValid(target)) { return null; }

		// 只给"植物"整体加白（与需求一致）；墓碑/坑/护盾等其它可铲对象保持原样。
		if (!(target is TowerDefensePlant)) { return null; }
		return target;
	}

	/// <summary>
	/// 兜底：照 `ShovelManager.ProcessShovelPick` 的公式自己算一次"鼠标下的可铲角色"：
	///     groundHeight = mapFeature.GetGroundHeight(cell)
	///     percentage   = (mouseWorld − GetMapCellPos(grid) + (0, groundHeight)).Y / GetMapGridSize().Y
	///     character    = cell.GetShovelCharacter(percentage, shovelConfig)
	/// 与手套那边的 `ProcessGloveSelect` 用的是同一套口径（已解包核对）。
	/// </summary>
	private TowerDefenseCharacter ResolveShovelHoverByMouse(TowerDefenseManager mgr)
	{
		TowerDefenseBattleFeatureMap mapFeature = null;
		try { mapFeature = TowerDefenseManager.GetMapFeature(); } catch { }
		if (mapFeature == null || !GodotObject.IsInstanceValid(mapFeature)) { return null; }
		TowerDefenseMapControl mapControl = null;
		try { mapControl = mapFeature.mapControl; } catch { }
		if (mapControl == null || !GodotObject.IsInstanceValid(mapControl)) { return null; }

		Vector2 world = Vector2.Zero;
		try { world = mapControl.GetGlobalMousePosition(); } catch { return null; }

		Vector2I gridPos = Vector2I.Zero;
		try { gridPos = mgr.GetMapGridPosFromMouse(world); } catch { return null; }
		bool inMap = false;
		try { inMap = mgr.CheckMapGridPosIn(gridPos); } catch { }
		if (!inMap) { return null; }

		TowerDefenseCellInstance cell = null;
		try { cell = TowerDefenseManager.GetMapCell(gridPos); } catch { }
		if (cell == null || !GodotObject.IsInstanceValid(cell)) { return null; }

		double groundHeight = 0.0;
		try { groundHeight = mapFeature.GetGroundHeight(cell); } catch { }
		Vector2 cellPos = Vector2.Zero;
		Vector2 gridSize = Vector2.Zero;
		try
		{
			cellPos = mgr.GetMapCellPos(gridPos);
			gridSize = mgr.GetMapGridSize();
		}
		catch { return null; }
		if (gridSize.Y <= 0f) { return null; }

		double percentage = (world - cellPos + new Vector2(0f, (float)groundHeight)).Y / gridSize.Y;
		try { return cell.GetShovelCharacter(percentage); } catch { return null; }
	}

	/// <summary>
	/// 白色遮罩强度。
	///
	/// 宿主把 `whiteStrength` 归到"亮度通道"里（`ShaderEffectComponent.IsBrightnessProperty` 命中
	/// `brightStrength|whiteStrength|toolHighlightStrength`），最终落到
	/// `ApplyBrightness()` → `SetRenderColorMultiplier(1+n, 1+n, 1+n)` → `Modulate = meshColor × (1+n)`。
	/// ⇒ 传 **1.0** 相当于整组 sprite 乘 2（明显过曝发白，即"整株变白"）。
	/// 若实机看着只是"变亮"而不够白，把这个常量调大即可（2.0 ⇒ ×3），逻辑不用改。
	/// </summary>
	private const float ShovelWhiteStrength = 1.0f;

	/// <summary>写角色 shader 的 `whiteStrength`（`ShovelWhiteStrength` = 整株变白，0 = 还原）。</summary>
	/// <param name="whiteStrength">要写入的强度（0 = 还原）。</param>
	private void WriteWhite(TowerDefenseCharacter character, float whiteStrength)
	{
		try
		{
			if (character == null || !GodotObject.IsInstanceValid(character)) { return; }
			character.SetSpriteGroupShaderParameter(WhiteStrengthParam, whiteStrength);
		}
		catch { }
	}

	// ================================================================ 铲子 / 手套 互斥

	/// <summary>
	/// ★ v1.0.2（用户反馈「为什么又能选铲子又能选手套？」）：
	///
	/// ── 根因（解包核对）────────────────────────────────────────────
	/// 铲子与手套是 `PacketPickControl.tools` 里的**两个独立工具**（`ShovelPickTool` / `GlovePickTool`，
	/// 都是 `PacketPickTool : Node`），而宿主 `PacketPickControl.ProcessTools`（:1221-1251）只处理
	/// "已选卡 ⇒ 释放工具"这一层，**从不处理工具之间的互斥** ⇒ 两个工具的 `IsPicking()` 可以同时为真，
	/// 玩家就能同时处于"铲子模式 + 手套模式"。
	///
	/// ── 本方法的做法：上升沿互斥（每帧调，开销极小）──────────────────
	///   · 手套 `glovePick` false→true ⇒ 取消铲子：`ShovelManager.ShovelReset()`
	///     （内部 `ApplyShovelPickState(false)`：清 `shovelPick`、按钮取消按下、藏掉地图铲子），
	///     再补 `shovelButton.SetPressedNoSignal(false)` 保证视觉；
	///   · 铲子 `shovelPick` false→true ⇒ 取消手套：`TowerDefenseBattleFeatureGlove.GloveReset()`
	///     （清 `pickedCharacter`、`glovePick=false`、藏地图手套、`SetGloveButtonPressed(false)`），
	///     再补一次 `gloveManager.SetGloveButtonPressed(false)`；
	///   · 同一帧两边都刚变 true：用"本帧刚按下的动作"判先后（`Glove` / `Shovel` 两个 InputAction），
	///     判不出来就**保留手套、取消铲子**（后变化者赢的默认取向，且手套是本 Mod 的功能入口）；
	///   · 两个都为 true 但都不是本帧的沿（残局，例如 Mod 中途加载）：只处理一次，
	///     保守**取消手套、保留游戏原生铲子**，并打日志。
	///
	/// 换关 / 重开 / 退出：`Feature` 实例会重建 ⇒ 用 `GetInstanceId()` 变化来复位沿状态，
	/// 避免上一关的 `true` 被当成本帧的上升沿。
	/// </summary>
	private void TickToolMutex()
	{
		TowerDefenseManager mgr = null;
		try { mgr = TowerDefenseManager.Instance; } catch { }
		if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { ResetMutexEdges(); return; }

		TowerDefenseControlNew ctrl = null;
		try { ctrl = mgr.currentControl; } catch { }
		if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { ResetMutexEdges(); return; }
		bool running = false;
		try { running = ctrl.isGameRunning; } catch { }
		if (!running) { ResetMutexEdges(); return; }

		TowerDefenseBattleFeatureGlove gloveFeature = null;
		try { gloveFeature = mgr.GetGloveFeature(); } catch { }
		if (gloveFeature == null || !GodotObject.IsInstanceValid(gloveFeature)) { ResetMutexEdges(); return; }

		TowerDefenseBattleFeatureShovel shovelFeature = null;
		try { shovelFeature = ctrl.GetFeature(new StringName(ShovelFeatureName)) as TowerDefenseBattleFeatureShovel; } catch { }
		ShovelManager shovelManager = null;
		if (shovelFeature != null && GodotObject.IsInstanceValid(shovelFeature))
		{
			try { shovelManager = shovelFeature.shovelManager; } catch { }
		}
		if (shovelManager == null || !GodotObject.IsInstanceValid(shovelManager))
		{
			// 铲子 Feature 不在（极少数关卡）⇒ 没有互斥对象，只维护手套这一侧的沿
			ResetMutexEdges();
			try { _prevGlovePick = gloveFeature.glovePick; _mutexGloveId = gloveFeature.GetInstanceId(); } catch { }
			return;
		}

		ulong gloveId = 0, shovelId = 0;
		try { gloveId = gloveFeature.GetInstanceId(); } catch { }
		try { shovelId = shovelManager.GetInstanceId(); } catch { }
		if (gloveId == 0 || shovelId == 0) { ResetMutexEdges(); return; }

		bool glovePick = false, shovelPick = false;
		try { glovePick = gloveFeature.glovePick; } catch { }
		try { shovelPick = shovelManager.shovelPick; } catch { }

		// 换关/重开（实例重建）⇒ 只记当前状态，**不**当成上升沿
		if (gloveId != _mutexGloveId || shovelId != _mutexShovelId)
		{
			_mutexGloveId = gloveId;
			_mutexShovelId = shovelId;
			_prevGlovePick = glovePick;
			_prevShovelPick = shovelPick;
			_mutexConflictHandled = false;
			return;
		}

		bool gloveRise = glovePick && !_prevGlovePick;
		bool shovelRise = shovelPick && !_prevShovelPick;
		_prevGlovePick = glovePick;
		_prevShovelPick = shovelPick;

		if (!(glovePick && shovelPick)) { _mutexConflictHandled = false; return; }
		if (_mutexConflictHandled) { return; }   // 残局只处理一次，等状态变化再说
		_mutexConflictHandled = true;

		if (gloveRise && !shovelRise)
		{
			CancelShovelTool(shovelManager, "手套刚被选中");
		}
		else if (shovelRise && !gloveRise)
		{
			CancelGloveTool(gloveFeature, "铲子刚被选中");
		}
		else if (gloveRise && shovelRise)
		{
			// 同一帧两边都变 true：用本帧刚触发的动作猜"谁后变"，判不出来就保留手套
			bool keyShovel = false, keyGlove = false;
			try { keyShovel = Input.IsActionJustPressed("Shovel"); } catch { }
			try { keyGlove = Input.IsActionJustPressed("Glove"); } catch { }
			if (keyShovel && !keyGlove) { CancelGloveTool(gloveFeature, "同帧且铲子动作更晚"); }
			else { CancelShovelTool(shovelManager, "同帧且手套动作更晚/无法判定"); }
		}
		else
		{
			// 残局：两个都是 true，但都不是本帧的沿 ⇒ 保守保留游戏原生铲子
			CancelGloveTool(gloveFeature, "检测到残局：优先保留原生铲子");
		}
	}

	/// <summary>复位沿状态（离开战斗 / 换关 / 工具实例变化时用）。</summary>
	private void ResetMutexEdges()
	{
		_prevGlovePick = false;
		_prevShovelPick = false;
		_mutexConflictHandled = false;
		_mutexGloveId = 0UL;
		_mutexShovelId = 0UL;
	}

	/// <summary>取消铲子：`ShovelReset()`（内部会清 `shovelPick`、取消按钮按下、藏地图铲子）。</summary>
	private void CancelShovelTool(ShovelManager shovelManager, string why)
	{
		try
		{
			if (shovelManager != null && GodotObject.IsInstanceValid(shovelManager))
			{
				shovelManager.ShovelReset();
				// 兜底：万一 ShovelReset 没把状态清干净，再显式放一次（PickShovel(false) 会播音效，
				// 所以只在还处于 picking 时才调）
				try { if (shovelManager.shovelPick) { shovelManager.PickShovel(false); } } catch { }
				try
				{
					TextureButton btn = shovelManager.shovelButton;
					if (btn != null && GodotObject.IsInstanceValid(btn)) { btn.SetPressedNoSignal(false); }
				}
				catch { }
			}
		}
		catch { }
		_prevShovelPick = false;
		Info("手套与铲子互斥：已取消铲子（" + why + "）");
	}

	/// <summary>
	/// 取消手套：先 `ClearPickedState()`（把"拿起中的植物"清掉、释放预览精灵），
	/// 再 `GloveReset()`（清 `glovePick`、藏掉地图手套、按钮复位），最后补一次
	/// `gloveManager.SetGloveButtonPressed(false)` 保证按钮视觉与状态一致。
	/// </summary>
	private void CancelGloveTool(TowerDefenseBattleFeatureGlove gloveFeature, string why)
	{
		try
		{
			if (gloveFeature != null && GodotObject.IsInstanceValid(gloveFeature))
			{
				// 显式两步（`GloveReset()` 内部其实也含 `ClearPickedState()`，这里写全是为了意图清楚、
				// 且若宿主未来改 GloveReset 的实现，清选择这一步仍然一定执行）
				try { gloveFeature.ClearPickedState(); } catch { }
				gloveFeature.GloveReset();
				try
				{
					GloveManager gm = gloveFeature.gloveManager;
					if (gm != null && GodotObject.IsInstanceValid(gm)) { gm.SetGloveButtonPressed(false); }
				}
				catch { }
			}
		}
		catch { }
		_prevGlovePick = false;
		Info("手套与铲子互斥：已取消手套（" + why + "）");
	}

	// ================================================================ 放置后屏蔽点击 + 真实外观预览

	/// <summary>
	/// 取"正在搬运"的状态：`feature.pickedCharacter` 有效即视为已拿起一株植物。
	/// 只在战斗运行中才认（避免菜单里拿到上个关卡的残留引用）。
	/// </summary>
	private bool TryGetGloveCarry(out TowerDefenseBattleFeatureGlove feature, out TowerDefenseCharacter carried)
	{
		feature = null;
		carried = null;
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return false; }
			TowerDefenseControlNew ctrl = mgr.currentControl;
			if (ctrl == null || !GodotObject.IsInstanceValid(ctrl)) { return false; }
			bool running = false;
			try { running = ctrl.isGameRunning; } catch { }
			if (!running) { return false; }
			feature = mgr.GetGloveFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature)) { feature = null; return false; }
			TowerDefenseCharacter c = null;
			try { c = feature.pickedCharacter; } catch { }
			if (c == null || !GodotObject.IsInstanceValid(c)) { return false; }
			carried = c;
			return true;
		}
		catch { feature = null; carried = null; return false; }
	}

	/// <summary>
	/// ★ v1.0.4（玩家复测「泡椒罐子用手套搬完还是被砸/触发」）：**手套模式开着期间，全场植物一律不响应点击**。
	///
	/// ── 为什么 v1.0.3 的"拿到 pickedCharacter 后再屏蔽"必然晚一拍 ──────────
	/// Godot 的 `_input`/`_unhandled_input` 派发在每帧 `process_frame` 信号**之前**，而"拿起某株植物"这一次
	/// press 与 `ProcessGloveSelect → SelectCharacter` 是**同一批输入**：
	///   · 泡椒罐 `TowerDefensePlantJalaVase.cs:75` 直接 `OnPressed += HandleVasePressed`（**按下就砸**）；
	///   · 加农炮同样挂 `OnPressed / OnFinishPressed`（`CannonComponent.cs:436-524`）。
	/// ⇒ 等我们"拿到 pickedCharacter"再 `SetAlive(false)`，罐子在同一帧已经被砸了。
	///
	/// ── 现在的语义（改"按时屏蔽"为"按模式屏蔽"）──────────────────────────
	/// 手套模式（`glovePick == true`）期间，**所有植物**的 `MousePressComponent` 一律 `SetAlive(false)`：
	///   · **激活沿**：`characterRegistry.GetCharactersIntersectingRectList(超大矩形)` 枚举全场
	///     → 挑 `is TowerDefensePlant` → `componentManager.GetRuntime<MousePressComponent>()`
	///     → **原本 `Alive == true` 的才关**，记进集合（instanceId → 组件 + 原本是否活着）；
	///   · **维护**：每 3 帧对"鼠标悬停格那株"再确保一次（`CannonComponent` 会自己 `SetAlive(...)` 改回来），
	///     每 ~0.5s 重扫全场一次（手套开着时新种下的植物也要屏蔽）；
	///   · **退出沿 / 换关 / 卸载**：集合里"我们关掉且原本是活的"全部 `SetAlive(true)` 还原；
	///   · 刚放置的那一株额外保留「250ms 且确认键已松开」窗口（避免"放下"那一次点击的余波）。
	/// 屏蔽只拦"点击"：不影响手套拾取（走 `GetShovelCharacter/CanShovel`）、不影响受击与正常战斗逻辑。
	///
	/// ⚠️ `GetCharactersIntersectingRectList` 返回的是注册表**内部复用缓冲**（`_rectQueryBuffer`），
	///   下一次查询会被覆盖 ⇒ 必须立刻遍历，绝不能把它存起来跨帧用。
	/// </summary>
	private void TickClickSuppression()
	{
		ulong now = Time.GetTicksMsec();

		// ── ① 搬运状态跟踪 ──
		TowerDefenseBattleFeatureGlove feature = null;
		TowerDefenseCharacter carried = null;
		bool carrying = TryGetGloveCarry(out feature, out carried);
		bool gloveMode = false;
		if (feature != null && GodotObject.IsInstanceValid(feature))
		{
			try { gloveMode = feature.glovePick; } catch { }
		}

		if (carrying)
		{
			ulong carriedId = 0UL;
			try { carriedId = carried.GetInstanceId(); } catch { }
			// 拿起那一帧：撤销"这次 press 已经排上队"的按键状态（只对这一株做一次）
			if (carriedId != 0UL && carriedId != _undoPressDoneForId)
			{
				_undoPressDoneForId = carriedId;
				UndoAccidentalPress(carried);
			}
			if (!_carriedActive || _carriedId != carriedId)
			{
				_carriedId = carriedId;
				_carriedChar = carried;
				_carriedGrid = SafeGridPos(carried);
				_carriedActive = true;
			}
		}
		else if (_carriedActive)
		{
			_carriedActive = false;
			// 同一实例的 gridPos 变了 = 真的放下去了（`MoveCharacterToCell` 复用同一实例并写 gridPos）
			bool placed = false;
			try
			{
				placed = _carriedChar != null && GodotObject.IsInstanceValid(_carriedChar)
					&& _carriedChar.gridPos != _carriedGrid;
			}
			catch { }
			if (placed && _carriedId != 0UL)
			{
				SetSuppressionWindow(_carriedId, now + CarryClickGuardMsec);
				Info("手套放置：刚放下的植物保持点击屏蔽 " + CarryClickGuardMsec + "ms（" + SafeCharacterName(_carriedChar) + "）");
			}
			else
			{
				Info("手套取消搬运：不再额外屏蔽点击");
			}
			_carriedChar = null;
			_undoPressDoneForId = 0UL;
		}

		// ── ② 手套模式开关：整场屏蔽 / 还原 ──
		if (gloveMode)
		{
			if (!_clickSupMode)
			{
				_clickSupMode = true;
				_clickSupHoverTick = 0;
				_clickSupRescanMsec = now + ClickSuppressRescanMsec;
				int n = SuppressAllPlants(now);
				Info("手套模式：已屏蔽 " + n + " 株植物的点击");
			}
			else
			{
				_clickSupHoverTick++;
				if (_clickSupHoverTick >= 3)
				{
					_clickSupHoverTick = 0;
					EnsureHoveredPlantSuppressed(now);
				}
				if (now >= _clickSupRescanMsec)
				{
					_clickSupRescanMsec = now + ClickSuppressRescanMsec;
					int extra = SuppressAllPlants(now);
					if (extra > 0) { Info("手套模式：重扫又屏蔽 " + extra + " 株植物的点击"); }
				}
			}
		}
		else if (_clickSupMode)
		{
			_clickSupMode = false;
			int n = RestoreAllSuppressed(now);
			Info("手套模式：已还原 " + n + " 株植物点击");
		}

		// ── ③ 窗口到期后的兜底还原（只在手套模式已关闭时发生）──
		RestoreExpiredSuppressions(now);
	}

	/// <summary>
	/// ★ v1.0.4 兜底（Lead 追加）：「拿起那一下」的 press 可能已经把触发状态排上队，
	/// 这里在**刚拿起**的那一帧做一次**事后撤销**（只做一次，不每帧刷）：
	///   · 反射找这株植物身上**名字恰为 `pressed` / `pressAwait` 的 public bool 字段**并置回 false
	///     （泡椒罐 `TowerDefensePlantJalaVase` 就有这两个：`pressed` 会挡住后续加泡椒、`pressAwait` 是防抖）；
	///   · `MousePressComponent` 若 `IsPressed == true`，直接 `SetAlive(false)` ——
	///     它会走 `OnAliveChanged(false)` → `InvalidatePendingInput()` + `ResetInteraction()`，
	///     把按下态/悬停亮度一并清掉（真正还原由"手套模式还原"统一负责）。
	/// 字段不存在 / 不可写 / 抛异常 ⇒ 安静跳过。
	/// </summary>
	private void UndoAccidentalPress(TowerDefenseCharacter character)
	{
		try
		{
			if (character == null || !GodotObject.IsInstanceValid(character)) { return; }
			bool changed = false;

			// ① public bool 字段：pressed / pressAwait
			System.Type t = character.GetType();
			for (System.Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
			{
				System.Reflection.FieldInfo[] fields = null;
				try { fields = cur.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); }
				catch { fields = null; }
				if (fields == null) { continue; }
				for (int i = 0; i < fields.Length; i++)
				{
					System.Reflection.FieldInfo f = fields[i];
					try
					{
						if (f.FieldType != typeof(bool)) { continue; }
						string name = f.Name;
						if (!(string.Equals(name, "pressed", StringComparison.Ordinal)
							|| string.Equals(name, "pressAwait", StringComparison.Ordinal)))
						{
							continue;
						}
						if (!(f.GetValue(character) is bool on) || !on) { continue; }
						f.SetValue(character, false);
						changed = true;
					}
					catch { }
				}
			}

			// ② MousePressComponent 的按下态（SetAlive(false) 会连带 ResetInteraction）
			try
			{
				ComponentManager manager = character.componentManager;
				MousePressComponent comp = (manager != null) ? manager.GetRuntime<MousePressComponent>() : null;
				if (comp != null && !comp.IsReleased && comp.IsPressed && comp.Alive)
				{
					comp.SetAlive(false);
					changed = true;
				}
			}
			catch { }

			if (changed)
			{
				Info("拿起时撤销误触发的按键状态：" + SafeCharacterName(character));
			}
		}
		catch { }
	}

	/// <summary>遍历全场植物，把"原本活着"的 `MousePressComponent` 关掉并记进集合。返回本次新屏蔽株数。</summary>
	private int SuppressAllPlants(ulong now)
	{
		int added = 0;
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return 0; }
			TowerDefenseBattleCharacterRegistry registry = null;
			try { registry = mgr.characterRegistry; } catch { }
			if (registry == null || !GodotObject.IsInstanceValid(registry)) { return 0; }

			List<TowerDefenseCharacter> list = null;
			try
			{
				list = registry.GetCharactersIntersectingRectList(new Rect2(-10000f, -10000f, 20000f, 20000f));
			}
			catch { return 0; }
			if (list == null) { return 0; }
			// ⚠️ 内部复用缓冲：先复制到自有缓冲再遍历（SetAlive 会回调宿主，可能改动注册表）
			_scanScratch.Clear();
			int n = 0;
			try { n = list.Count; } catch { n = 0; }
			for (int i = 0; i < n; i++)
			{
				TowerDefenseCharacter c = null;
				try { c = list[i]; } catch { continue; }
				if (c == null || !GodotObject.IsInstanceValid(c)) { continue; }
				if (!(c is TowerDefensePlant)) { continue; }
				_scanScratch.Add(c);
			}
			for (int i = 0; i < _scanScratch.Count; i++)
			{
				if (SuppressOnePlant(_scanScratch[i], now)) { added++; }
			}
			_scanScratch.Clear();
		}
		catch { }
		return added;
	}

	/// <summary>
	/// 屏蔽单株。已有记录 ⇒ 若被宿主改回 `Alive`（`CannonComponent` 会自己 `SetAlive(...)`）就再关一次；
	/// 新记录只在"原本活着的"情况下建立（不碰本来就是死的组件）。
	/// </summary>
	private bool SuppressOnePlant(TowerDefenseCharacter character, ulong now)
	{
		try
		{
			if (character == null || !GodotObject.IsInstanceValid(character)) { return false; }
			bool dead = false;
			try { dead = character.die || character.isDestroy; } catch { }
			if (dead) { return false; }

			ulong id = 0UL;
			try { id = character.GetInstanceId(); } catch { }
			if (id == 0UL) { return false; }

			ClickSuppressionEntry exist;
			if (_clickSup.TryGetValue(id, out exist))
			{
				try
				{
					if (exist != null && exist.Comp != null && !exist.Comp.IsReleased && exist.WasAlive && exist.Comp.Alive)
					{
						exist.Comp.SetAlive(false);
					}
				}
				catch { }
				return false;
			}

			MousePressComponent comp = null;
			try
			{
				ComponentManager manager = character.componentManager;
				if (manager != null) { comp = manager.GetRuntime<MousePressComponent>(); }
			}
			catch { }
			if (comp == null || comp.IsReleased)
			{
				WarnNoMousePressOnce(SafeCharacterName(character));
				return false;
			}
			bool alive = false;
			try { alive = comp.Alive; } catch { }
			if (!alive) { return false; }        // 本来就不是活的 ⇒ 不归我们管

			comp.SetAlive(false);
			ClickSuppressionEntry e = new ClickSuppressionEntry();
			e.Comp = comp;
			e.WasAlive = true;
			e.UntilMsec = 0UL;
			e.Name = SafeCharacterName(character);
			_clickSup[id] = e;
			return true;
		}
		catch { return false; }
	}

	/// <summary>每 3 帧对"鼠标悬停格那一株"再确保一次屏蔽（宿主会自己把 `Alive` 改回来）。</summary>
	private void EnsureHoveredPlantSuppressed(ulong now)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return; }
			TowerDefenseBattleFeatureMap mapFeature = null;
			try { mapFeature = TowerDefenseManager.GetMapFeature(); } catch { }
			if (mapFeature == null || !GodotObject.IsInstanceValid(mapFeature)) { return; }
			TowerDefenseMapControl mapControl = null;
			try { mapControl = mapFeature.mapControl; } catch { }
			if (mapControl == null || !GodotObject.IsInstanceValid(mapControl)) { return; }

			Vector2 world = Vector2.Zero;
			try { world = mapControl.GetGlobalMousePosition(); } catch { return; }
			Vector2I gridPos = Vector2I.Zero;
			try { gridPos = mgr.GetMapGridPosFromMouse(world); } catch { return; }
			bool inMap = false;
			try { inMap = mgr.CheckMapGridPosIn(gridPos); } catch { }
			if (!inMap) { return; }
			TowerDefenseCellInstance cell = null;
			try { cell = TowerDefenseManager.GetMapCell(gridPos); } catch { }
			if (cell == null || !GodotObject.IsInstanceValid(cell)) { return; }

			// 与铲子/手套同一套"格内取角色"公式（`ShovelManager.ProcessShovelPick`）
			double groundHeight = 0.0;
			try { groundHeight = mapFeature.GetGroundHeight(cell); } catch { }
			Vector2 cellPos = Vector2.Zero;
			Vector2 gridSize = Vector2.Zero;
			try
			{
				cellPos = mgr.GetMapCellPos(gridPos);
				gridSize = mgr.GetMapGridSize();
			}
			catch { return; }
			if (gridSize.Y <= 0f) { return; }
			double percentage = (world - cellPos + new Vector2(0f, (float)groundHeight)).Y / gridSize.Y;

			TowerDefenseCharacter hovered = null;
			try { hovered = cell.GetShovelCharacter(percentage); } catch { }
			if (hovered != null && GodotObject.IsInstanceValid(hovered) && hovered is TowerDefensePlant)
			{
				SuppressOnePlant(hovered, now);
			}
		}
		catch { }
	}

	/// <summary>给某株（刚放下的）延长屏蔽窗口。</summary>
	private void SetSuppressionWindow(ulong id, ulong untilMsec)
	{
		try
		{
			ClickSuppressionEntry e;
			if (_clickSup.TryGetValue(id, out e) && e != null) { e.UntilMsec = untilMsec; }
		}
		catch { }
	}

	/// <summary>手套模式关闭：把集合里"我们关掉且原本是活的"全部还原（刚放下的那株等窗口到期）。</summary>
	private int RestoreAllSuppressed(ulong now)
	{
		int restored = 0;
		try
		{
			List<ulong> done = new List<ulong>();
			foreach (KeyValuePair<ulong, ClickSuppressionEntry> kv in _clickSup)
			{
				ClickSuppressionEntry e = kv.Value;
				if (e == null) { done.Add(kv.Key); continue; }
				if (e.UntilMsec > now) { continue; }     // 刚放下那株的窗口，留给 RestoreExpiredSuppressions
				try
				{
					if (e.Comp != null && !e.Comp.IsReleased)
					{
						if (e.WasAlive && !e.Comp.Alive) { e.Comp.SetAlive(true); restored++; }
						else if (e.WasAlive) { restored++; }
					}
				}
				catch { }
				done.Add(kv.Key);
			}
			for (int i = 0; i < done.Count; i++) { _clickSup.Remove(done[i]); }
		}
		catch { }
		return restored;
	}

	/// <summary>窗口到期 + 确认键已松开 ⇒ 还原（手套模式关闭后才会真正还原）。</summary>
	private void RestoreExpiredSuppressions(ulong now)
	{
		if (_clickSup.Count == 0 || _clickSupMode) { return; }
		if (IsConfirmHeld()) { return; }
		List<ulong> done = new List<ulong>();
		try
		{
			foreach (KeyValuePair<ulong, ClickSuppressionEntry> kv in _clickSup)
			{
				ClickSuppressionEntry e = kv.Value;
				bool valid = false;
				try { valid = e != null && e.Comp != null && !e.Comp.IsReleased; } catch { }
				if (!valid) { done.Add(kv.Key); continue; }
				if (now < e.UntilMsec) { continue; }
				try
				{
					if (e.WasAlive && !e.Comp.Alive) { e.Comp.SetAlive(true); }
				}
				catch { }
				Info("已还原点击屏蔽（窗口结束）：" + e.Name);
				done.Add(kv.Key);
			}
			for (int i = 0; i < done.Count; i++) { _clickSup.Remove(done[i]); }
		}
		catch { }
	}

	/// <summary>离开战斗/卸载时无条件还原所有屏蔽（不看窗口）。</summary>
	private int RestoreAllSuppressedForce(string why)
	{
		int restored = 0;
		try
		{
			foreach (KeyValuePair<ulong, ClickSuppressionEntry> kv in _clickSup)
			{
				ClickSuppressionEntry e = kv.Value;
				try
				{
					if (e != null && e.Comp != null && !e.Comp.IsReleased && e.WasAlive && !e.Comp.Alive)
					{
						e.Comp.SetAlive(true);
						restored++;
					}
				}
				catch { }
			}
			_clickSup.Clear();
			if (restored > 0 || _clickSupMode)
			{
				Info("手套模式：已还原 " + restored + " 株植物点击（" + why + "）");
			}
		}
		catch { }
		_clickSupMode = false;
		_clickSupHoverTick = 0;
		_clickSupRescanMsec = 0UL;
		_carriedActive = false;
		_carriedChar = null;
		_carriedId = 0UL;
		_undoPressDoneForId = 0UL;
		return restored;
	}

	private void WarnNoMousePressOnce(string plantName)
	{
		if (!_log) { return; }
		try
		{
			if (string.IsNullOrEmpty(plantName)) { plantName = "<未知>"; }
			if (!_noMousePressWarned.Add(plantName)) { return; }
			Info("取不到 MousePressComponent：" + plantName);
		}
		catch { }
	}

	/// <summary>确认键是否还按着（PC = `Press` 动作 / 鼠标左键；手机 = 触摸按下）。</summary>
	private static bool IsConfirmHeld()
	{
		try
		{
			if (Input.IsActionPressed("Press")) { return true; }
		}
		catch { }
		try
		{
			if (Input.IsMouseButtonPressed(MouseButton.Left)) { return true; }
		}
		catch { }
		return false;
	}

	private static Vector2I SafeGridPos(TowerDefenseCharacter character)
	{
		try
		{
			if (character != null && GodotObject.IsInstanceValid(character)) { return character.gridPos; }
		}
		catch { }
		return Vector2I.Zero;
	}

	private static string SafeCharacterName(TowerDefenseCharacter character)
	{
		try
		{
			if (character == null || !GodotObject.IsInstanceValid(character)) { return "<已销毁>"; }
			TowerDefenseCharacterConfig cfg = character.config;
			if (cfg != null && GodotObject.IsInstanceValid(cfg) && !string.IsNullOrEmpty(cfg.name)) { return cfg.name; }
		}
		catch { }
		return "<未知>";
	}

	/// <summary>
	/// ★ v1.0.3（玩家反馈「移动毁灭加农炮时 为什么显示的是最基础的玉米加农炮」）：
	///
	/// ── 根因（解包核对）────────────────────────────────────────────
	/// 宿主预览走 `GloveManager.CreatePreviewSprite` → `TowerDefenseManager.GetCharacterSprite(character.config.name)`，
	/// **按 config.name 重新 Instantiate 一份默认精灵场景**；而变体植物（毁灭加农炮 / 皮肤 / 杂交形态）的
	/// 真实外观来自**该实例**的图集替换与形态状态（`flashAnimeData` / `atlasProfileOverride` /
	/// `mediaReplaceAtlas*` 等）⇒ 预览自然画成基础玉米加农炮。
	///
	/// ── 做法（任务里的方案①：克隆真实 sprite）──────────────────────
	///   `pickedCharacter.sprite.Duplicate()` 一份挂到 `mapControl.spriteNode`，半透明（沿用宿主的
	///   `Modulate.a = 0.5` + `meshColor.a = 0.5`）、`ZIndex = 1000`，每帧**照抄宿主 Process 的定位逻辑**
	///   （能放 ⇒ 贴目标格 `GetMapCellPlantPos`，不能放 ⇒ 跟鼠标 `spriteNode.GetLocalMousePosition() - (0,20)`）。
	///
	///   ⚠️ `Duplicate()` 只复制 **`[Export]` 属性**；`mediaReplaceAtlas*` 这些"公开但非导出"的运行时字段
	///   必须自己再补一遍（见 `CopyPreviewVisualState`），否则图集替换类的变体仍会掉回默认外观。
	///   ⚠️ 克隆是否可用**不能只在编译期判断** ⇒ 建好后下一帧做一次健康检查
	///   （比较克隆与本体 `canvasItem`/`meshTexture` 这两个公开 Rid 有没有都初始化）；
	///   判定失败就丢掉克隆、调 `gloveManager.CreatePreviewSprite` 恢复宿主默认预览，并打一条明确 Warn。
	/// </summary>
	private void TickRealPreview()
	{
		TowerDefenseBattleFeatureGlove feature;
		TowerDefenseCharacter carried;
		bool carrying = TryGetGloveCarry(out feature, out carried);

		if (!carrying)
		{
			ClearPreviewClone();
			_previewCarryId = 0UL;
			_previewTried = false;
			_previewHealthyChecked = false;
			return;
		}

		ulong carriedId = 0UL;
		try { carriedId = carried.GetInstanceId(); } catch { }
		if (_previewCarryId != carriedId)
		{
			// 新的一次搬运：清掉上一次的克隆与"试过一次"标记
			ClearPreviewClone();
			_previewCarryId = carriedId;
			_previewTried = false;
			_previewHealthyChecked = false;
		}

		if (_previewClone == null && !_previewTried && !_previewFallback)
		{
			_previewTried = true;
			TryBuildPreviewClone(feature, carried);
		}

		if (_previewClone != null && !GodotObject.IsInstanceValid(_previewClone))
		{
			_previewClone = null;
		}

		// 健康检查（建好的下一帧做一次；失败则回退宿主默认预览）
		if (_previewClone != null && !_previewHealthyChecked)
		{
			_previewHealthyChecked = true;
			if (!PreviewCloneLooksReady(carried))
			{
				AbandonPreviewClone(feature, carried, "克隆的渲染管线没有初始化");
			}
		}

		if (_previewClone != null && GodotObject.IsInstanceValid(_previewClone))
		{
			PositionPreviewClone(feature, carried);
		}
	}

	/// <summary>用本体 sprite 的克隆替换宿主那个"按名字重造"的默认预览。</summary>
	private void TryBuildPreviewClone(TowerDefenseBattleFeatureGlove feature, TowerDefenseCharacter carried)
	{
		try
		{
			AdobeAnimateSprite src = null;
			try { src = carried.sprite; } catch { }
			if (src == null || !GodotObject.IsInstanceValid(src))
			{
				Info("真实外观预览：拿不到本体的 sprite，保持宿主默认预览。");
				return;
			}

			TowerDefenseBattleFeatureMap mapFeature = null;
			try { mapFeature = TowerDefenseManager.GetMapFeature(); } catch { }
			if (mapFeature == null || !GodotObject.IsInstanceValid(mapFeature)) { return; }
			TowerDefenseMapControl mapControl = null;
			try { mapControl = mapFeature.mapControl; } catch { }
			if (mapControl == null || !GodotObject.IsInstanceValid(mapControl)
				|| mapControl.spriteNode == null || !GodotObject.IsInstanceValid(mapControl.spriteNode)) { return; }

			AdobeAnimateSprite dup = null;
			try { dup = src.Duplicate() as AdobeAnimateSprite; }
			catch (Exception ex) { SafeWarn("真实外观预览：Duplicate() 失败（已吞）：" + ex.Message); }
			if (dup == null || !GodotObject.IsInstanceValid(dup))
			{
				SafeWarn("真实外观预览：Duplicate() 没拿到 sprite，保持宿主默认预览（外观可能与本体不一致）。");
				return;
			}

			// Duplicate 只复制 [Export]；图集/材质替换这些"非导出"的运行时状态再补一遍
			CopyPreviewVisualState(src, dup);

			// 外观参数与宿主预览保持一致（半透明、层级、不参与光照）
			try { dup.Visible = true; } catch { }
			try { dup.ZIndex = 1000; } catch { }
			try { dup.LightMask = 0; } catch { }
			try
			{
				Color m = dup.Modulate;
				m.A = 0.5f;
				dup.Modulate = m;
			}
			catch { }
			try
			{
				Color mc = dup.meshColor;
				mc.A = 0.5f;
				dup.meshColor = mc;
			}
			catch { }
			try { dup.Position = new Vector2(-100f, -100f); } catch { }

			mapControl.spriteNode.AddChild(dup, false, Node.InternalMode.Disabled);
			_previewClone = dup;
			_previewHealthyChecked = false;

			// 克隆建好之后，才把宿主那个默认外观的预览释放掉（`FreePreviewSprite` 会把字段置 null，
			// 宿主 Process 之后会因 `IsInstanceValid(null)` 提前 return，不再覆盖我们的定位）
			try
			{
				if (feature != null && GodotObject.IsInstanceValid(feature))
				{
					GloveManager gm = feature.gloveManager;
					if (gm != null && GodotObject.IsInstanceValid(gm)) { gm.FreePreviewSprite(); }
				}
			}
			catch { }

			Info("真实外观预览：已用本体 sprite 克隆（" + DescribePreview(src, dup) + "），宿主默认预览已释放。");
		}
		catch (Exception ex)
		{
			SafeWarn("真实外观预览异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>
	/// 把本体 sprite 的"非导出"运行时外观状态补到克隆上
	/// （`Duplicate()` 只复制 `[Export]` 属性；图集替换类变体全靠这些字段）。
	/// </summary>
	private void CopyPreviewVisualState(AdobeAnimateSprite src, AdobeAnimateSprite dup)
	{
		if (src == null || dup == null) { return; }
		try
		{
			try { if (dup.flashAnimeData == null) { dup.flashAnimeData = src.flashAnimeData; } } catch { }
			try { if (dup.atlasProfileOverride == null) { dup.atlasProfileOverride = src.atlasProfileOverride; } } catch { }
			try { dup.mediaReplaceAtlas = src.mediaReplaceAtlas; } catch { }
			try { dup.mediaReplaceAtlasArray = src.mediaReplaceAtlasArray; } catch { }
			try { dup.mediaReplaceAtlasArraySize = src.mediaReplaceAtlasArraySize; } catch { }
			try { dup.mediaReplaceAtlasUsesTextureArray = src.mediaReplaceAtlasUsesTextureArray; } catch { }
			try { dup.mediaReplaceRect = src.mediaReplaceRect; } catch { }
			try { dup.mediaReplaceAtlasPages = src.mediaReplaceAtlasPages; } catch { }
			try { dup.mediaReplaceAtlasShared = src.mediaReplaceAtlasShared; } catch { }
			try { dup.needMediaReplaceUpdate = true; } catch { }
			try { dup.frameIndex = src.frameIndex; } catch { }
			try { dup.clipRange = src.clipRange; } catch { }
			try { dup.loop = src.loop; } catch { }
			try { dup.frameRate = src.frameRate; } catch { }
		}
		catch { }
	}

	/// <summary>克隆的健康检查：本体的 Rid 已初始化而克隆没有 ⇒ 视为克隆不可用。</summary>
	private bool PreviewCloneLooksReady(TowerDefenseCharacter carried)
	{
		try
		{
			if (_previewClone == null || !GodotObject.IsInstanceValid(_previewClone)) { return false; }
			AdobeAnimateSprite src = null;
			try { src = (carried != null && GodotObject.IsInstanceValid(carried)) ? carried.sprite : null; } catch { }
			if (src == null || !GodotObject.IsInstanceValid(src)) { return true; }   // 无从比较，放过
			bool srcReady = false;
			try { srcReady = src.canvasItem.IsValid || src.meshTexture.IsValid; } catch { }
			if (!srcReady) { return true; }    // 本体也走 GPU 通道没有自建 canvasItem ⇒ 不判
			bool dupReady = false;
			try { dupReady = _previewClone.canvasItem.IsValid || _previewClone.meshTexture.IsValid; } catch { }
			return dupReady;
		}
		catch { return true; }
	}

	/// <summary>克隆不可用 ⇒ 丢掉它、恢复宿主默认预览，并在本关卡内不再重试。</summary>
	private void AbandonPreviewClone(TowerDefenseBattleFeatureGlove feature, TowerDefenseCharacter carried, string why)
	{
		try
		{
			if (_previewClone != null && GodotObject.IsInstanceValid(_previewClone)) { _previewClone.QueueFree(); }
		}
		catch { }
		_previewClone = null;
		_previewFallback = true;
		SafeWarn("真实外观预览：" + why + "，已回退到宿主默认预览（变体植物外观可能与本体不一致）。");
		try
		{
			if (feature != null && GodotObject.IsInstanceValid(feature))
			{
				GloveManager gm = feature.gloveManager;
				if (gm != null && GodotObject.IsInstanceValid(gm)) { gm.CreatePreviewSprite(carried); }
			}
		}
		catch { }
	}

	private string DescribePreview(AdobeAnimateSprite src, AdobeAnimateSprite dup)
	{
		try
		{
			return "子节点 " + dup.GetChildCount() + "/" + src.GetChildCount()
				+ "，flashAnimeData=" + ((dup.flashAnimeData != null) ? "有" : "无")
				+ "，atlasProfile=" + ((dup.atlasProfileOverride != null) ? "有" : "无");
		}
		catch { return "?"; }
	}

	/// <summary>每帧定位克隆（照抄 `TowerDefenseBattleFeatureGlove.Process` 的那段逻辑）。</summary>
	private void PositionPreviewClone(TowerDefenseBattleFeatureGlove feature, TowerDefenseCharacter carried)
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr)) { return; }
			TowerDefenseBattleFeatureMap mapFeature = null;
			try { mapFeature = TowerDefenseManager.GetMapFeature(); } catch { }
			if (mapFeature == null || !GodotObject.IsInstanceValid(mapFeature)) { return; }
			TowerDefenseMapControl mapControl = null;
			try { mapControl = mapFeature.mapControl; } catch { }
			if (mapControl == null || !GodotObject.IsInstanceValid(mapControl)
				|| mapControl.spriteNode == null || !GodotObject.IsInstanceValid(mapControl.spriteNode)) { return; }

			Vector2 mouse = Vector2.Zero;
			try { mouse = mapControl.GetGlobalMousePosition(); } catch { return; }
			Vector2I gridPos = Vector2I.Zero;
			try { gridPos = mgr.GetMapGridPosFromMouse(mouse); } catch { return; }

			Vector2I sourceGrid = new Vector2I(-999, -999);
			try { sourceGrid = feature.sourceGridPos; } catch { }

			bool canPlace = false;
			if (gridPos != sourceGrid)
			{
				bool inMap = false;
				try { inMap = mgr.CheckMapGridPosIn(gridPos); } catch { }
				if (inMap)
				{
					TowerDefenseCellInstance cell = null;
					try { cell = TowerDefenseManager.GetMapCell(gridPos); } catch { }
					if (cell != null && GodotObject.IsInstanceValid(cell)
						&& carried != null && GodotObject.IsInstanceValid(carried))
					{
						TowerDefensePacketConfig packet = null;
						try { packet = carried.packet; } catch { }
						if (packet != null && GodotObject.IsInstanceValid(packet))
						{
							try { canPlace = cell.CanPacketPlant(packet); } catch { canPlace = false; }
						}
					}
				}
			}

			if (canPlace)
			{
				Vector2 cellPos = Vector2.Zero;
				try { cellPos = TowerDefenseManager.GetMapCellPlantPos(gridPos); } catch { return; }
				_previewClone.GlobalPosition = cellPos;
			}
			else
			{
				_previewClone.Position = mapControl.spriteNode.GetLocalMousePosition() - new Vector2(0f, 20f);
			}
		}
		catch { }
	}

	/// <summary>释放克隆（搬运结束 / 换关 / 卸载时）。</summary>
	private void ClearPreviewClone()
	{
		try
		{
			if (_previewClone != null && GodotObject.IsInstanceValid(_previewClone)) { _previewClone.QueueFree(); }
		}
		catch { }
		_previewClone = null;
	}

	// ================================================================ 战斗设置页（PosEditorRow）

	/// <summary>扫战斗设置页对话框（`tree.Paused` 时才有），幂等注入「手套·按钮位置」按钮 + 两行位置编辑器。</summary>
	private void EnsureOptionRows()
	{
		if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }
		// 设置页 `pasue=true` ⇒ 平时用 `tree.Paused` 当廉价判据；
		// ⚠️ 多人模式下 `BattleOption._Ready` 会把 pasue 置 false 并解除暂停 ⇒ 只判 Paused 会永不注入。
		if (!_tree.Paused && !IsMultiplayerMode()) { return; }
		Node root = _tree.Root;
		if (root == null || !GodotObject.IsInstanceValid(root)) { return; }

		List<Node> opts = new List<Node>(4);
		CollectBattleOptionDialogs(root, opts, 4, 0);
		for (int i = 0; i < opts.Count; i++)
		{
			Node opt = opts[i];
			if (opt == null || !GodotObject.IsInstanceValid(opt)) { continue; }
			EnsurePosEditor(opt);
		}
	}

	/// <summary>递归收集名字以 `BattleOption` 开头的节点（对话框每次打开都是新实例）。</summary>
	private static void CollectBattleOptionDialogs(Node root, List<Node> outList, int max, int depth)
	{
		try
		{
			if (root == null || outList == null || outList.Count >= max || depth > 24) { return; }
			if (!GodotObject.IsInstanceValid(root)) { return; }
			int n = root.GetChildCount();
			for (int i = 0; i < n && outList.Count < max; i++)
			{
				Node ch = root.GetChild(i);
				if (ch == null || !GodotObject.IsInstanceValid(ch)) { continue; }
				string nm = ch.Name.ToString();
				if (nm.StartsWith("BattleOption", StringComparison.Ordinal))
				{
					outList.Add(ch);
					continue;
				}
				CollectBattleOptionDialogs(ch, outList, max, depth + 1);
			}
		}
		catch { }
	}

	private static bool IsMultiplayerMode()
	{
		try { return Global.IsMultiplayerMode; }
		catch { return false; }
	}

	/// <summary>两行「横版 · 位置」「竖版 · 位置」（共享组件 PosEditorRow，改动即时生效 + 写配置）。</summary>
	private void EnsurePosEditor(Node optionRoot)
	{
		try
		{
			float bw = GloveW;
			float bh = GloveH;
			try
			{
				TowerDefenseManager mgr = TowerDefenseManager.Instance;
				if (mgr != null && GodotObject.IsInstanceValid(mgr))
				{
					TowerDefenseBattleFeatureGlove feature = mgr.GetGloveFeature();
					if (feature != null && GodotObject.IsInstanceValid(feature)
						&& feature.gloveManager != null && GodotObject.IsInstanceValid(feature.gloveManager))
					{
						Vector2 s = feature.gloveManager.Size;
						if (s.X > 1f) { bw = s.X; }
						if (s.Y > 1f) { bh = s.Y; }
					}
				}
			}
			catch { }

			// ★ 同一个面板里两行即可（按钮文字与面板标题都带「手套」前缀，面板由本 Mod 独占）。
			PosEditorRow.Ensure(optionRoot, PosSpec, "glove_land", "横版 · 位置",
				PosPresetValues, PosPresetLabels, PosCustomValue,
				NormalizeMode(_landMode), _landX, _landY, bw, bh, ApplyLandFromEditor);

			PosEditorRow.Ensure(optionRoot, PosSpec, "glove_port", "竖版 · 位置",
				PosPresetValues, PosPresetLabels, PosCustomValue,
				NormalizeMode(_portMode), _portX, _portY, bw, bh, ApplyPortFromEditor);
		}
		catch (Exception ex)
		{
			SafeWarn("位置编辑器注入异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>横版那一行改动 ⇒ 写配置 + 当帧生效（下一帧 MaintainGlove 就会用新值定位）。</summary>
	private void ApplyLandFromEditor(string preset, int x, int y)
	{
		try
		{
			_landMode = NormalizeMode(preset);
			_landX = Mathf.Clamp(x, 0, 100);
			_landY = Mathf.Clamp(y, 0, 100);
			Dictionary<string, object> changes = new Dictionary<string, object>(3);
			changes[KLandMode] = _landMode;
			changes[KLandX] = _landX;
			changes[KLandY] = _landY;
			SaveSettings(changes);
			Info("横版位置 → " + _landMode + "(" + _landX + "%," + _landY + "%)。");
		}
		catch { }
	}

	/// <summary>竖版那一行改动 ⇒ 写配置 + 当帧生效。</summary>
	private void ApplyPortFromEditor(string preset, int x, int y)
	{
		try
		{
			_portMode = NormalizeMode(preset);
			_portX = Mathf.Clamp(x, 0, 100);
			_portY = Mathf.Clamp(y, 0, 100);
			Dictionary<string, object> changes = new Dictionary<string, object>(3);
			changes[KPortMode] = _portMode;
			changes[KPortX] = _portX;
			changes[KPortY] = _portY;
			SaveSettings(changes);
			Info("竖版位置 → " + _portMode + "(" + _portX + "%," + _portY + "%)。");
		}
		catch { }
	}

	// ================================================================ 日志

	private void Info(string msg)
	{
		if (!_log) { return; }
		try { GD.Print(LogPrefix + msg); } catch { }
	}

	private void SafeWarn(string msg)
	{
		try { GD.PrintErr(LogPrefix + msg); } catch { }
	}
}
