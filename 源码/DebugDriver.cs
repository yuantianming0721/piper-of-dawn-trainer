using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PiperTrainer;

// v4.17.0 DebugDriver：进程内 UI 自动推进（仅开发调试用，配置门控，默认关闭）。
// 背景：外部合成输入（SendInput/keybd_event/PostMessage/SetCursorPos）进不了本游戏的
// 新输入系统（UnityEngine.InputSystem + InputSystemUIInputModule），无法从外部驱动菜单。
// 插件在游戏进程内，直接走游戏自己的事件派发：
//   1) 菜单页：枚举 UnityEngine.UI.Button，名字/路径命中关键词→onClick.Invoke()；
//      无关键词匹配时轮转点未知按钮（仅测试副本）；
//   2) 开始页（无按钮）：Piper.ThePiper 主体组件挂 IPointerClickHandler（学习版 kdl /
//      Steam ker），直接调用=点屏幕任意处。
// 完成条件：SaveTreeReady()（存档树真正就绪=已读档进游戏），达成即停止。
// 全程 try/catch + 次数上限；默认关闭，正常使用无任何影响。
public static class DebugDriver
{
    public static bool Enabled;
    public static bool PopOkOnce;          // 允许一次性点击 UI_Pop 的 mBtnOK（读取确认）
    public static bool ScoutSlots;         // 侦察模式：逐槽点击读弹窗文本再取消，建 槽位↔存档号 映射
    public static int TargetSaveSlot = -1; // >0 时：侦察时命中该存档号则直接确认读取（如 9=读存档9）
    private static bool _popOkDone;
    private static int _scoutPhase;
    private static float _scoutAt = -99f;
    private static int _scoutIdx;
    private static bool _scoutDone;
    private static List<string> _scoutLeaves;
    public static string Status = "关闭";
    private static float _lastTry = -99f;
    private static int _tries;
    private static int _dumps;
    private static int _blindAt = 6;
    private static string _lastMiss = "";
    private static readonly string[] Keywords = { "继续", "开始", "进入", "新游戏", "新故事", "载入", "读档", "存档", "开始游戏",
        "start", "continue", "load", "newgame", "new_game", "play", "开始游戏", "点击", "任意", "save (", "save(" };
    private static readonly string[] Negative = { "退出", "离开", "设置", "设定", "商店", "exit", "quit", "setting", "option", "credits", "制作" };

    private static void RawLog(string m) => PiperTrainerPlugin.Log2?.LogInfo("UITEST " + m);

    public static void Tick()
    {
        if (!Enabled) return;
        try
        {
            if (PiperBridge.SaveTreeReady())
            {
                if (Status != "完成：已进游戏") { Status = "完成：已进游戏"; RawLog("完成：存档树就绪（已读档），停止推进。共尝试 " + _tries + " 次"); }
                return;
            }
        }
        catch { }
        float now = Time.realtimeSinceStartup;
        if (_tries >= 80) return;
        if (_lastTry < 0) _lastTry = now;
        if (now - _lastTry < 6f) return;
        _lastTry = now;
        _tries++;
        try
        {
            var buttons = FindButtons();
            // 侦察模式：等读档界面出现后逐槽侦察（读确认弹窗文本→取消），建 槽位↔存档号 映射
            if (ScoutSlots && !_scoutDone)
            {
                bool atLoadScreen = false;
                foreach (var b in buttons)
                    if (b.Path.Contains("UI_SaveProgress") && LeafOf(b.Path).StartsWith("save")) { atLoadScreen = true; break; }
                if (atLoadScreen) { ScoutTick(buttons, now); return; }
                // 否则走正常流程（点 continueGame 等到达读档界面）
            }
            // 0) 一次性：点击 UI_Pop 的 mBtnOK（读取确认对话框）
            if (PopOkOnce && !_popOkDone)
            {
                foreach (var b in buttons)
                {
                    if (b.Path.Contains("UI_Pop") && LeafOf(b.Path).Contains("mbtnok") && b.Interactable)
                    {
                        if (b.Click()) { _popOkDone = true; RawLog("已点击弹窗确认(mBtnOK)，等待读档"); Status = "已确认读档，等待加载"; return; }
                    }
                }
            }
            if (_popOkDone)
            {
                Status = "已确认读档；等待加载完成（不再点击）";
                return;   // 确认后进入只读等待，防止误触加载后的界面
            }
            if (_dumps < 12 && buttons.Count > 0)
            {
                _dumps++;
                foreach (var b in buttons) RawLog("按钮[" + _dumps + "]: " + b.Path + "  interactable=" + b.Interactable + " onClick=" + b.HandlerCount);
            }
            if (buttons.Count > 0)
            {
                // 优先级：continueGame/继续 > startGame/开始 > 载入/读档 > 其它关键词。只看叶子名，防祖先路径误匹配。
                for (int pass = 0; pass < 4; pass++)
                {
                    foreach (var b in buttons)
                    {
                        if (b.Skip) continue;
                        var leaf = LeafOf(b.Path);
                        bool hit = false;
                        switch (pass)
                        {
                            case 0: hit = leaf.Contains("continue") || leaf.Contains("继续"); break;
                            case 1: hit = leaf.Contains("startgame") || leaf.Contains("新游戏") || leaf.Contains("开始游戏"); break;
                            case 2: hit = leaf.Contains("载入") || leaf.Contains("读档") || leaf.Contains("load") || leaf.Contains("存档"); break;
                            case 3:
                                foreach (var k in Keywords) if (leaf.Contains(k.ToLowerInvariant())) { hit = true; break; }
                                break;                        }
                        if (!hit) continue;
                        if (b.Click()) { RawLog("已点按钮: " + b.Path); Status = "已点击 " + b.Path; return; }
                    }
                }
                // 多次无命中后轮转点未知按钮（测试副本专用策略；跳过 UI_Pop/负关键词）
                if (_tries >= _blindAt)
                {
                    _blindAt = _tries + 2;
                    for (int i = 0; i < buttons.Count; i++)
                    {
                        var pick = buttons[(_tries + i) % buttons.Count];
                        if (pick.Skip || !pick.Interactable) continue;
                        if (pick.Click()) { RawLog("盲点按钮: " + pick.Path); Status = "盲点 " + pick.Path; return; }
                    }
                }
                Status = "有按钮 " + buttons.Count + " 个，无关键词命中";
                return;
            }
            // 3) 无按钮：开始页点击处理器
            var miss = "";
            if (ClickStartPage(ref miss)) { Status = "已点开始页"; return; }
            if (miss != _lastMiss) { _lastMiss = miss; RawLog("开始页处理器未生效: " + miss); }
            Status = "无按钮/处理器未命中(" + miss + ")";
        }
        catch (Exception e) { RawLog("Tick err " + e.Message); Status = "err " + e.Message; }
    }

    // ---------------- 侦察模式：逐槽点击读弹窗文本（不真正读档） ----------------
    private static void ScoutTick(List<Btn> buttons, float now)
    {
        try
        {
            switch (_scoutPhase)
            {
                case 0:
                    if (now - _scoutAt < 1.5f) return;
                    _scoutAt = now;
                    _scoutLeaves = new List<string>();
                    foreach (var b in buttons)
                    {
                        var leaf = LeafOf(b.Path);
                        if (b.Path.Contains("UI_SaveProgress") && (leaf.StartsWith("save") || leaf.StartsWith("存档")))
                            _scoutLeaves.Add(b.Path);
                    }
                    if (_scoutLeaves.Count == 0)
                    {
                        _scoutIdx++;
                        if (_scoutIdx > 10) { RawLog("侦察：等待槽位超时，放弃"); _scoutDone = true; _tries = 999; return; }
                        return;   // 界面还在加载，留待下一轮重试
                    }
                    _scoutIdx = 0;
                    RawLog("侦察：读档页槽位 " + _scoutLeaves.Count + " 个");
                    foreach (var p in _scoutLeaves) RawLog("  槽位: " + p);
                    RawLog("侦察：当前页面文本: " + DumpTmpTexts("UI_SaveProgress"));
                    _scoutPhase = 1;
                    return;
                case 1:
                    if (_scoutIdx >= _scoutLeaves.Count) { RawLog("侦察完成（共 " + _scoutLeaves.Count + " 槽）"); _scoutDone = true; _tries = 999; Status = "侦察完成"; return; }
                    {
                        var target = _scoutLeaves[_scoutIdx];
                        foreach (var b in buttons)
                            if (b.Path == target) { b.Click(); break; }
                        RawLog("侦察：已点槽位 " + target);
                    }
                    _scoutPhase = 2; _scoutAt = now;
                    return;
                case 2:
                    if (now - _scoutAt < 0.7f) return;
                    {
                        var bl = FindButtons();   // 弹窗是点击后才创建的，必须重新枚举
                        var txt = DumpTmpTexts("UI_Pop");
                        if (txt.Length == 0 && now - _scoutAt < 2.8f) return;   // 弹窗可能晚一点出现，继续等
                        int no = ParseSaveNo(txt);
                        RawLog("侦察：槽位[" + _scoutIdx + "] " + LeafOf(_scoutLeaves[_scoutIdx]) + " → 存档号 " + (no < 0 ? "?" : no.ToString()) + "  文本: " + (txt.Length == 0 ? "(无弹窗)" : txt));
                        if (TargetSaveSlot > 0 && no == TargetSaveSlot)
                        {
                            RawLog("★ 命中目标：存档 " + no + " @ " + LeafOf(_scoutLeaves[_scoutIdx]) + "，保留确认弹窗，交由 mBtnOK 读取");
                            _scoutDone = true; Status = "命中存档 " + no;
                            return;   // 不取消、不停止驱动：由正常流程的 PopOkOnce 点确认开始读取
                        }
                        foreach (var b in bl)
                            if (b.Path.Contains("UI_Pop") && LeafOf(b.Path).Contains("mbtncancel")) { b.Click(); break; }
                    }
                    _scoutPhase = 3; _scoutAt = now;
                    return;
                case 3:
                    if (now - _scoutAt < 1.0f) return;
                    _scoutIdx++;
                    _scoutPhase = 1; _scoutAt = now;
                    return;
            }
        }
        catch (Exception e) { RawLog("侦察 err " + e.Message); }
    }

    // 从弹窗文本里解析「存档 N」的 N
    private static int ParseSaveNo(string txt)
    {
        try
        {
            int i = txt.IndexOf("存档");
            if (i < 0) return -1;
            i += 2;
            while (i < txt.Length && !char.IsDigit(txt[i])) i++;
            int s = i;
            while (i < txt.Length && char.IsDigit(txt[i])) i++;
            if (i == s) return -1;
            return int.Parse(txt.Substring(s, i - s));
        }
        catch { return -1; }
    }

    // 读取指定路径前缀下所有文本（TMP + 旧版 Text 都扫，拼成一行）
    private static string DumpTmpTexts(string pathFilter)
    {
        var sb = new System.Text.StringBuilder();
        // 1) TMP
        try
        {
            var it = Il2CppSystem.Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
            if (it != null)
            {
                var arr = UnityEngine.Object.FindObjectsOfType(it);
                if (arr != null)
                    for (int i = 0; i < arr.Length && i < 160; i++)
                    {
                        try
                        {
                            var tmp = arr[i].TryCast<TMPro.TextMeshProUGUI>();
                            if (tmp == null) continue;
                            string path = PathOf(tmp.transform);
                            if (!path.Contains(pathFilter)) continue;
                            string t = tmp.text;
                            if (!string.IsNullOrEmpty(t)) sb.Append('{').Append(t).Append("} ");
                        }
                        catch { }
                    }
            }
        }
        catch { }
        // 2) 旧版 UnityEngine.UI.Text
        try
        {
            var it2 = Il2CppSystem.Type.GetType("UnityEngine.UI.Text, UnityEngine.UI");
            if (it2 != null)
            {
                var arr2 = UnityEngine.Object.FindObjectsOfType(it2);
                if (arr2 != null)
                    for (int i = 0; i < arr2.Length && i < 200; i++)
                    {
                        try
                        {
                            var lt = arr2[i].TryCast<UnityEngine.UI.Text>();
                            if (lt == null) continue;
                            string path = PathOf(lt.transform);
                            if (!path.Contains(pathFilter)) continue;
                            string t = lt.text;
                            if (!string.IsNullOrEmpty(t)) sb.Append('{').Append(t).Append("} ");
                        }
                        catch { }
                    }
            }
        }
        catch { }
        return sb.ToString();
    }

    // 开始页点击：找 Piper.ThePiper 实例，调用其 (IPointerClickHandler, BaseEventData) 处理器
    private static bool ClickStartPage(ref string miss)
    {
        try
        {
            // v4.17.5：用未混淆类型取 Assembly-CSharp（旧 Piper.rl 已随版本改名）
            var t = typeof(SaveLoadSystem.AllStaffData).Assembly.GetType("Piper.ThePiper");
            if (t == null) { miss = "类型 Piper.ThePiper 不存在"; return false; }
            var it = Il2CppSystem.Type.GetType(t.FullName + ", Assembly-CSharp");
            if (it == null) { miss = "il2cpp 类型解析失败"; return false; }
            var arr = UnityEngine.Object.FindObjectsOfType(it);
            if (arr == null || arr.Length == 0) { miss = "未找到 ThePiper 实例"; return false; }
            var inst = arr[0].TryCast<UnityEngine.Component>();   // 泛型包装→强类型，方法表才可见
            if (inst == null) { miss = "实例 TryCast 失败"; return false; }
            MethodInfo handler = null;
            foreach (var m in inst.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType.Name == "IPointerClickHandler") { handler = m; break; }
            }
            if (handler == null) { miss = "无 IPointerClickHandler 方法（实例类型 " + inst.GetType().Name + "）"; return false; }
            object data = null;
            try { data = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current); } catch { }
            handler.Invoke(inst, new object[] { inst, data });
            RawLog("已调用 " + inst.GetType().Name + "." + handler.Name + "（开始页点击，第" + _tries + "次）");
            return true;
        }
        catch (Exception e) { miss = "异常 " + e.Message; return false; }
    }

    private class Btn
    {
        public string Path; public bool Interactable; public int HandlerCount; public bool Skip; public Func<bool> Click;
    }

    private static string LeafOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int i = path.LastIndexOf('/');
        return (i >= 0 ? path.Substring(i + 1) : path).ToLowerInvariant();
    }

    private static List<Btn> FindButtons()
    {
        var list = new List<Btn>();
        try
        {
            var it = Il2CppSystem.Type.GetType("UnityEngine.UI.Button, UnityEngine.UI");
            if (it == null) return list;
            var arr = UnityEngine.Object.FindObjectsOfType(it);
            if (arr == null) return list;
            for (int i = 0; i < arr.Length && i < 60; i++)
            {
                try
                {
                    var btn = arr[i].TryCast<UnityEngine.UI.Button>();   // 关键：泛型包装→强类型
                    if (btn == null) continue;
                    var go = btn.gameObject;
                    if (go == null) continue;
                    var path = PathOf(go.transform);
                    bool act = false;
                    try { act = btn.interactable; } catch { }
                    int hc = -1;
                    try { hc = btn.onClick.GetPersistentEventCount(); } catch { }
                    var btnC = btn;
                    var click = new Func<bool>(() =>
                    {
                        try
                        {
                            btnC.onClick.Invoke();
                            return true;
                        }
                        catch (Exception e) { RawLog("click fail " + path + ": " + e.Message); return false; }
                    });
                    // 跳过：弹窗 UI_Pop 内按钮（可能含退出确认）、退出/设置类、分享按钮、页码按钮
                    var leaf = LeafOf(path);
                    bool skip = path.Contains("UI_Pop") || path.Contains("Share/")
                        || leaf.Contains("close") || leaf.Contains("quit") || leaf.Contains("exit")
                        || leaf.Contains("setting") || leaf.Contains("share") || leaf.StartsWith("page");
                    list.Add(new Btn { Path = path, Interactable = act, HandlerCount = hc, Skip = skip, Click = click });
                }
                catch { }
            }
        }
        catch (Exception e) { RawLog("FindButtons err " + e.Message); }
        return list;
    }

    private static string PathOf(UnityEngine.Transform t)
    {
        try
        {
            var s = t.name;
            var p = t.parent;
            int depth = 0;
            while (p != null && depth++ < 6) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }
        catch { return "?"; }
    }
}
