using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PiperTrainer;

// v4.10: 全程序集管理器扫描器
// 遍历 il2cpp 全部类型的静态字段/属性，找出各运行时管理器实例（rl/Staff.bbe/Favor.bmd/SaveLoadManager/ban…）
// il2cpp 原生反射可读到托管反射看不到的真实静态数据（已实测：SLM._instance ✓）
public static class ManagerSweep
{
    public static Piper.rl Rl;
    public static Piper.Staff.bbe StaffBbe;
    public static SaveLoadSystem.SaveLoadManager Slm;

    public static int Scanned;
    public static bool Done;

    private static readonly string[] WantNames = { "bbe", "ban" };

    public static string Sweep()
    {
        // v4.12.1 永久禁用：2026-10-10 实测，逐类型 Invoke 静态属性 getter 会触发某些类型的
        // il2cpp 原生类初始化失败，托管 try/catch 拦不住，直接带崩游戏进程（2000/3083 处崩）
        try { PiperTrainerPlugin.Log2?.LogWarning("MGRSWEEP 已禁用（v4.12 实测崩游戏），如需实例请走定向持有链分析"); } catch { }
        return "⚠ 扫描器已禁用：上次实测会崩游戏（逐类型 Invoke 触发原生初始化崩溃）";
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

    private static Piper.rl TryCastPiperRl(Il2CppSystem.Object o)
    {
        try { return o.TryCast<Piper.rl>(); } catch { return null; }
    }
}
