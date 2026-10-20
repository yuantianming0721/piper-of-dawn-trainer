using System;
using System.IO;
using UnityEngine;

namespace PiperTrainer;

// v4.17.1 DebugCapture：截图巡检（仅开发调试，配置门控，默认关闭）。
// 用途：为宣传片抓取真实修改器界面截图——游戏进程内自动：等进游戏并连接 → 开菜单 →
// 拉高分辨率到 4K → 逐页签截图 → 员工页抓「回满前/回满后」对比 → 关菜单抓一张干净场景 → 恢复。
// 配置：[调试] 截图巡检=true / 截图目录=E:/...（用正斜杠）
public static class DebugCapture
{
    public static bool Enabled;
    public static string Dir = "";
    public static string Status = "关闭";

    private static float _tick = -99f;
    private static int _phase;
    private static float _phaseAt;
    private static bool _done;
    private static bool _resRaised;
    private static int _origW, _origH;
    private static int[] _tabs = { 0, 1, 2, 4, 3 };   // 金钱/背包/好感/建筑/员工(最后做前后对比)
    private static int _tabIdx;

    private static void RawLog(string m) => PiperTrainerPlugin.Log2?.LogInfo("SHOT " + m);

    private static string Path_(string name) => (Dir.TrimEnd('/') + "/" + name);

    public static void Tick()
    {
        if (!Enabled || _done) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _tick < 0.5f) return;
        _tick = now;
        try
        {
            switch (_phase)
            {
                case 0: // 等读档进游戏 + 理智连接且同步已稳定
                    if (PiperBridge.SaveTreeReady() && StaffLive.Synced)
                    {
                        RawLog("就绪：存档树✓ 理智连接✓（员工 " + "?）→ 开始巡检");
                        _phase = 1; _phaseAt = now;
                    }
                    else if (now - _phaseAt > 240f)
                    {
                        RawLog("等待超时（SaveTree=" + PiperBridge.SaveTreeReady() + " Staff=" + StaffLive.Ready + "），放弃");
                        _done = true;
                    }
                    return;

                case 1: // 拉高分辨率 + 开菜单
                    _origW = UnityEngine.Screen.width; _origH = UnityEngine.Screen.height;
                    try { UnityEngine.Screen.SetResolution(3840, 2160, UnityEngine.FullScreenMode.Windowed); _resRaised = true; }
                    catch (Exception e1)
                    {
                        try { UnityEngine.Screen.SetResolution(3840, 2160, false); _resRaised = true; }
                        catch (Exception e2) { RawLog("SetResolution 失败: " + e1.Message + " / " + e2.Message); }
                    }
                    PiperTrainerPlugin.SetVisible(true);
                    RawLog("菜单已开，分辨率 " + _origW + "x" + _origH + " → 尝试 3840x2160");
                    _phase = 2; _phaseAt = now; _tabIdx = 0;
                    return;

                case 2: // 逐页签截图
                    if (now - _phaseAt < 2.5f) return;
                    if (_tabIdx < _tabs.Length)
                    {
                        int tab = _tabs[_tabIdx];
                        TrainerUI.DebugSwitchTab(tab);
                        _phase = 3; _phaseAt = now;
                        return;
                    }
                    _phase = 5; _phaseAt = now;   // 页签做完 → 员工页前后对比
                    return;

                case 3: // 页签已切好，落一张
                    {
                        int tab = _tabs[_tabIdx];
                        string name = "面板_" + TabName(tab) + ".png";
                        Shot(name);
                        RawLog("已截图 " + name + "（屏幕 " + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height + "）");
                        _tabIdx++;
                        _phase = 2; _phaseAt = now;
                        return;
                    }

                case 5: // 员工页：回满前
                    TrainerUI.DebugSwitchTab(3);
                    _phase = 6; _phaseAt = now;
                    return;

                case 6:
                    if (now - _phaseAt < 2.5f) return;
                    Shot("面板_员工_回满前.png");
                    RawLog("员工页(回满前)已截图");
                    _phase = 7; _phaseAt = now;
                    return;

                case 7: // 执行回满
                    {
                        string r = StaffLive.SetAllFull();
                        RawLog("执行回满: " + r);
                        _phase = 8; _phaseAt = now;
                        return;
                    }

                case 8:
                    if (now - _phaseAt < 1.2f) return;
                    TrainerUI.DebugSwitchTab(3);   // 刷新面板行数据（与用户点按钮后的刷新同路径）
                    _phase = 81; _phaseAt = now;
                    return;

                case 81:
                    if (now - _phaseAt < 1.0f) return;
                    Shot("面板_员工_回满后.png");
                    RawLog("员工页(回满后)已截图");
                    _phase = 9; _phaseAt = now;
                    return;

                case 9: // 关菜单，抓一张干净游戏画面
                    PiperTrainerPlugin.SetVisible(false);
                    _phase = 10; _phaseAt = now;
                    return;

                case 10:
                    if (now - _phaseAt < 1.5f) return;
                    Shot("游戏内_干净.png");
                    RawLog("游戏内干净画面已截图");
                    _phase = 11; _phaseAt = now;
                    return;

                case 11: // 恢复分辨率 + 收尾
                    try { if (_resRaised) UnityEngine.Screen.SetResolution(_origW, _origH, UnityEngine.FullScreenMode.Windowed); } catch { }
                    RawLog("巡检完成（输出目录 " + Dir + "）");
                    Status = "完成";
                    _done = true;
                    Enabled = false;
                    return;
            }
        }
        catch (Exception e) { RawLog("err " + e.Message); }
    }

    private static string TabName(int t)
    {
        switch (t) { case 0: return "金钱"; case 1: return "背包"; case 2: return "好感"; case 3: return "员工"; case 4: return "建筑"; default: return "x" + t; }
    }

    private static void Shot(string name)
    {
        try
        {
            var dir = Dir.Replace('\\', '/').TrimEnd('/');
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            UnityEngine.ScreenCapture.CaptureScreenshot(Path_(name));
        }
        catch (Exception e) { RawLog("截图失败 " + name + ": " + e.Message); }
    }
}
