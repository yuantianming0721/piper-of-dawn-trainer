using System;
using HarmonyLib;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace PiperTrainer;

[BepInPlugin("local.coldbrew.piper.trainer", "吹笛人内置修改器 (IL2CPP)", "4.17.9")]
public class PiperTrainerPlugin : BasePlugin
{
    public static PiperTrainerPlugin Instance;
    public static ManualLogSource Log2;
    private ConfigEntry<int> _hotkey;
    public static ConfigEntry<int> WinW, WinH;
    private static bool _show;
    private static bool _updateLogged, _onguiLogged;

    public override void Load()
    {
        Instance = this;
        Log2 = Log;
        // 用枚举让编译器取正确值（Home=278）；cfg 里存 int 避免 interop 枚举进 toml 的兼容问题
        _hotkey = Config.Bind("通用", "热键KeyCode", (int)KeyCode.Home, "打开/关闭修改器菜单（UnityEngine.KeyCode 值）");
        // 窗口大小（v4.17.9）：玩家可调，写入 cfg 持久化
        try { WinW = Config.Bind("通用", "窗口宽度", 560, "修改器窗口宽度（像素，560~1600，默认 560）"); } catch { }
        try { WinH = Config.Bind("通用", "窗口高度", 700, "修改器窗口高度（像素，700~1200，默认 700；只可调大）"); } catch { }
        // UI 自动推进（仅开发调试；进程内模拟点击开始页/菜单按钮，正常使用保持 false）
        try { DebugDriver.Enabled = Config.Bind("调试", "自动推进开始页", false, "开发调试用：进程内自动模拟点击开始页与主菜单按钮").Value; } catch { }
        try { DebugDriver.PopOkOnce = Config.Bind("调试", "点击弹窗确认一次", false, "开发调试用：一次性点击读取确认弹窗(UI_Pop/mBtnOK)").Value; } catch { }
        try { DebugDriver.ScoutSlots = Config.Bind("调试", "侦察槽位", false, "开发调试用：逐槽点击读档弹窗文本再取消，建立槽位↔存档号映射").Value; } catch { }
        try { DebugDriver.TargetSaveSlot = Config.Bind("调试", "读档目标存档号", 0, "开发调试用：>0 时侦察命中该存档号后自动确认读取（如 9=读存档9）").Value; } catch { }
        try { StaffLive.VerifyOnce = Config.Bind("调试", "自动回满验证一次", false, "开发调试用：锁定字段后自动执行一次全员回满并回读校验").Value; } catch { }
        try { StaffLive.InsanitySelfTest = Config.Bind("调试", "失智复位自测", false, "开发调试用：随机取一名员工置为失智再执行复位自测").Value; } catch { }
        try { PiperBridge.FeedbackSelfTest = Config.Bind("调试", "玩家反馈自测", false, "开发调试用：加钱/加好感/加种子各一次并回读校验").Value; } catch { }
        try { DebugCapture.Enabled = Config.Bind("调试", "截图巡检", false, "开发调试用：自动开菜单逐页签截图（含员工页回满前后对比）").Value; } catch { }
        try { DebugCapture.Dir = Config.Bind("调试", "截图目录", "", "截图巡检输出目录（正斜杠，如 E:/out/shots/）").Value; } catch { }

        // v4.17.9：应用玩家配置的窗口尺寸（clamp 在 ApplySize 内）
        try { TrainerUI.ApplySize(WinW == null ? 560 : WinW.Value, WinH == null ? 700 : WinH.Value); } catch { }

        ClassInjector.RegisterTypeInIl2Cpp<TrainerComponent>();


        // pre.2 自带的组件挂载助手：内部创建 GameObject 并 DontDestroyOnLoad
        AddComponent<TrainerComponent>();

        PiperNames.LoadCaches();
        // STAFFLIVE：人柴理智实时引擎（钩子捕获管理器/实体 + 字段oracle + 运行时直写，见 StaffLive.cs）
        try { StaffLive.Install(new Harmony("local.coldbrew.piper.trainer.stafflive")); }
        catch (Exception e) { Log.LogError("StaffLive 安装失败: " + e); }
        Log.LogInfo("PiperTrainer (IL2CPP) 已加载，按 Home 打开菜单");
    }

    public const string Ver = "4.17.9";

    public static int HotkeyCode => Instance == null || Instance._hotkey == null ? (int)KeyCode.Home : Instance._hotkey.Value;
    public static void Toggle() => _show = !_show;

    // v4.17.9：面板底部尺寸按钮调用；clamp 后应用并写回 cfg（ConfigEntry setter 自动保存）
    public static void SetWindowSize(int w, int h)
    {
        try
        {
            TrainerUI.ApplySize(w, h);
            if (WinW != null) WinW.Value = Mathf.RoundToInt(TrainerUI.PanelRect.width);
            if (WinH != null) WinH.Value = Mathf.RoundToInt(TrainerUI.PanelRect.height);
        }
        catch (Exception e) { Log2?.LogWarning("SetWindowSize: " + e.Message); }
    }
    public static void SetVisible(bool v) => _show = v;
    public static bool Visible => _show;
}

// 注入到 il2cpp 的组件，负责收 Update/OnGUI 回调
public class TrainerComponent : MonoBehaviour
{
    private static bool _updateLogged, _onguiLogged;

    public TrainerComponent(IntPtr ptr) : base(ptr) { }

    private bool _esBlocked;
    private UnityEngine.EventSystems.EventSystem _cachedEs;
    private int _nameTick;

    // 鼠标在修改器窗口内时禁用游戏 EventSystem，防止点穿误触游戏 UI
    private void GateEventSystem()
    {
        try
        {
            if (_cachedEs == null)
            {
                var found = UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>();
                if (found != null && found.Length > 0) _cachedEs = found[0];
            }
            var es = _cachedEs;
            if (es == null) return;
            bool over = false;
            if (PiperTrainerPlugin.Visible)
            {
                var m = Input.mousePosition;
                var guiPos = new Vector3(m.x, Screen.height - m.y, 0);
                over = TrainerUI.PanelRect.Contains(guiPos);
                // 搜索输入框区域不拦（uGUI 点击要能到达）：豁免矩形=输入框实际位置（面板右侧外部），
                // 与 OnGUI 里 UguiInput.Sync 用的 ExternalSearchRect 保持一致
                if (over && (TrainerUI.CurrentTab == 1 || TrainerUI.CurrentTab == 4) && TrainerUI.ExternalSearchRect.Contains(guiPos)) over = false;
                if (over && UguiInput.IsFocused()) over = false;
            }
            // 每帧强制同步（自愈）：悬停面板→禁用；其余情况确保开启
            if (over)
            {
                if (es.enabled) es.enabled = false;
                _esBlocked = true;
            }
            else
            {
                if (_esBlocked && !es.enabled) es.enabled = true;
                _esBlocked = false;
            }
        }
        catch { }
    }

    private float _bootTime;
    private bool _sweptOnce;
    private int _rlTries;
    private float _lastTry;
    private bool _probedOnce;
    private int _probeTries;
    private float _lastProbeTry;

    public void Update()
    {
        GateEventSystem();
        // 结构探测：等存档树真正就绪（GSE 出现得比 BagSaveData 早，只看 GSE 会打太早），
        // 就绪 3 秒后自动跑一次；未就绪每 10 秒重试，最多 30 次
        try
        {
            if (!_probedOnce && _probeTries < 30 && Time.realtimeSinceStartup - _lastProbeTry > 10)
            {
                if (PiperBridge.SaveTreeReady())
                {
                    if (_bootTime == 0) _bootTime = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - _bootTime > 3)
                    {
                        _probeTries++;
                        _lastProbeTry = Time.realtimeSinceStartup;
                        if (PiperBridge.DumpStructures(true)) _probedOnce = true;
                    }
                }
                else
                {
                    _probeTries++;
                    _lastProbeTry = Time.realtimeSinceStartup;
                }
            }
        }
        catch { }
        // 启动 8 秒后做一次 IL2CPP 原生反射扫描（找 rl 运行时单例），只做一次
        if (_bootTime == 0) _bootTime = Time.realtimeSinceStartup;
        if (Time.realtimeSinceStartup - _bootTime > 8 && !PiperBridge.FoundRl && _rlTries < 20 && Time.realtimeSinceStartup - _lastTry > 8)
        {
            _rlTries++;
            _lastTry = Time.realtimeSinceStartup;
            try { PiperBridge.Il2cppSweep(); } catch { }
        }
        try
        {
            if (!_updateLogged)
            {
                _updateLogged = true;
                PiperTrainerPlugin.Log2?.LogInfo($"Update 回调正常，热键 KeyCode={PiperTrainerPlugin.HotkeyCode}");
            }
            if (Input.GetKeyDown((KeyCode)PiperTrainerPlugin.HotkeyCode))
            {
                PiperTrainerPlugin.Toggle();
                PiperTrainerPlugin.Log2?.LogInfo($"修改器菜单: {(PiperTrainerPlugin.Visible ? "开" : "关")}");
            }
            // 自动解析名字表（每 ~3 秒尝试一次，成功后不再重试）
            _nameTick++;
            if (_nameTick % 180 == 0) PiperNames.TryInit();
            StaffLive.Tick();
            DebugDriver.Tick();
            DebugCapture.Tick();
        }
        catch (Exception e)
        {
            PiperTrainerPlugin.Log2?.LogError("update: " + e);
        }
    }

    public void OnDestroy()
    {
        try
        {
            if (_cachedEs != null && !_cachedEs.enabled) _cachedEs.enabled = true;
        }
        catch { }
    }

    public void OnGUI()
    {
        try { PiperBridge.DrawToasts(); }
        catch { }
        try
        {
            // 打字搜索框：背包页显示在面板右侧外部
        try
        {
            // uGUI 搜索框显隐与定位
            UguiInput.Sync(PiperTrainerPlugin.Visible && (TrainerUI.CurrentTab == 1 || TrainerUI.CurrentTab == 4), TrainerUI.ExternalSearchRect);
        }
        catch { }
        }
        catch { }
        if (!PiperTrainerPlugin.Visible) return;
        try
        {
            if (!_onguiLogged)
            {
                _onguiLogged = true;
                PiperTrainerPlugin.Log2?.LogInfo("OnGUI 渲染正常");
            }
            TrainerUI.Draw();
        }
        catch (Exception e)
        {
            PiperTrainerPlugin.Log2?.LogError("ongui: " + e);
        }
    }
}
