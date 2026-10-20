using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PiperTrainer;

// v4.17.0 STAFFLIVE：人柴理智「实时进程」修改引擎（机制透视版）
//
// 【机制事实（2026-10-14 截图+日志实锤）】
// 1. 存档树 StaffData.CurrentSan/MaxSan 是毫单位（×1000）：树 1250000/1250000 ↔ 游戏内显示 1250/1250；
// 2. 存档树会长期陈旧：autosave 很不频繁（实测整场会话未触发），树里躺的可能是很久以前的旧值；
// 3. 游戏 UI 读的是运行时实体（学习版 Piper.Staff.baw），改树对它零影响；
// 4. 实体有 bbj 状态枚举（Insane/Resting/Working/Idle）+ 3 个 int 读写属性 + 1 个 Int64（LastSanUpdateTime 候选），
//    理智字段名被混淆，必须用"实体↔存档条目同步瞬间"的值相等来钉死（oracle）。
//
// 【主链路（不再等 autosave）】
//   管理器捕获（LoadAllStaffData 钩子 / SaveAllStaffData 钩子 / dfi<T> 静态属性）→
//   主动调用游戏自己的 AllStaffData.SaveAllStaffData(管理器)：
//     · 这是 autosave 的同一段代码（运行时→存档树的单向同步），只动内存树不落盘、不重建任何系统；
//     · 游戏内部会逐员工调用 SaveStaffEntity(实体, StaffData)，本插件的 postfix 钩子即拿到
//       全部(实体, 存档条目)对——实体清单与完美 oracle 一次到手，无需枚举管理器内部字典。
//   多对投票锁字段：对全部(实体,条目)对统计"实体哪个属性 == sd.CurrentSan / MaxSan / LastSanUpdateTime"，
//   命中率 ≥80% 且唯一领先者胜出；兼容实体以毫单位或显示单位存储（×1000 等价也算命中）。
//   兜底：max 值双射（树 MaxSan 稳定）+ 60 秒双快照下降检测。
//
// 【写入】仅 interop 属性 setter 直改实体字段；时间戳纪元实测判定后才写；绝不调 Load()、绝不落盘。
public static class StaffLive
{
    public static string Status = "未连接（读档进游戏后自动连接，或点[连接运行时]）";
    public static bool Hooked;
    public static object Manager;
    public static Type ManagerType;
    public static bool VerifyOnce;         // 调试验证：锁定后自动执行一次回满并回读校验
    private static bool _verifyDone;

    public static bool MaxLocked => _maxProp != null;
    public static bool Ready => _curProp != null && _maxProp != null && _pairs.Count > 0;
    public static bool Synced => Ready && !_resyncPending;   // 已同步且无待处理的载入事件
    public static string FieldInfo => (_curProp ?? "?") + "/" + (_maxProp ?? "?");

    private class Pair { public object Ent; public SaveLoadSystem.StaffData Sd; public int Idx; }
    private static readonly List<Pair> _pairs = new List<Pair>();

    private static string _curProp, _maxProp, _tsProp;
    private static string _tsEpoch;
    private static bool _scaledCur, _scaledMax;      // 实体值×1000==存档值（毫单位换算）
    private static long _epochSample1; private static float _epochAt1 = -1f; private static bool _epochDone;

    private static readonly Dictionary<string, int> _maxVotes = new Dictionary<string, int>();
    private static readonly Dictionary<string, int> _maxScaled = new Dictionary<string, int>();
    private static readonly Dictionary<string, int> _curVotes = new Dictionary<string, int>();
    private static readonly Dictionary<string, int> _curScaled = new Dictionary<string, int>();
    private static readonly Dictionary<string, int> _tsVotes = new Dictionary<string, int>();
    private static int _votePairs;

    private static bool _probeFired, _probeFailed;
    private static bool _resyncPending;              // 有载入事件，待重新同步（v4.17.2）
    private static bool _inProbe;                    // 正在执行同步探针（忽略自家捕获）
    private static int _resyncAttempts;              // 单轮同步重试计数（不匹配时重试）
    private static float _lastMgrCaptureAt = -99f;   // 最近一次管理器捕获时刻（静默窗口起算）
    private static float _managerAt = -1f, _lastStaticTry = -99f;
    private static int _staticTries;
    private static bool _diagDumped, _enumFallbackTried, _staticWarned;
    private static float _snapAt = -1f; private static Dictionary<object, int> _snapCur; private static bool _decreaseLogged;
    private static readonly Dictionary<Type, PropertyInfo[]> _intCache = new Dictionary<Type, PropertyInfo[]>();
    private static readonly Dictionary<Type, PropertyInfo[]> _longCache = new Dictionary<Type, PropertyInfo[]>();
    private static readonly Dictionary<Type, PropertyInfo[]> _enumCache = new Dictionary<Type, PropertyInfo[]>();

    private static void RawLog(string m) => PiperTrainerPlugin.Log2?.LogInfo(m);

    // ---------------- 钩子安装 ----------------
    public static void Install(Harmony harmony)
    {
        try
        {
            var t = typeof(SaveLoadSystem.AllStaffData);
            int patched = 0;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!m.DeclaringType.Equals(t)) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && (m.Name == "SaveAllStaffData" || m.Name == "LoadAllStaffData"))
                {
                    try { ManagerType = ps[0].ParameterType; } catch { }
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(StaffLive), nameof(PostManager)));
                    patched++;
                }
                if (ps.Length == 2 && m.Name == "SaveStaffEntity")
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(StaffLive), nameof(PostEntity)));
                    patched++;
                }
            }
            Hooked = patched > 0;
            if (!Hooked) Status = "钩子安装失败（未找到 AllStaffData 目标方法）";
            RawLog("STAFFLIVE hook " + (Hooked ? "OK x" + patched : "失败") + (ManagerType == null ? "" : " 管理器类型(签名)=" + ManagerType.FullName));
        }
        catch (Exception e)
        {
            Status = "钩子异常: " + e.Message;
            PiperTrainerPlugin.Log2?.LogError("STAFFLIVE install: " + e);
        }
    }

    private static void PostManager(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 1 || __args[0] == null) return;
            var mgr = __args[0];
            if (Manager == null || Manager != mgr)
                RawLog("STAFFLIVE 捕获管理器: " + mgr.GetType().FullName);
            Manager = mgr;
            ManagerType = mgr.GetType();
            if (_managerAt < 0) _managerAt = UnityEngine.Time.realtimeSinceStartup;
            if (!_inProbe)
            {
                // v4.17.2：任何一次真实载入/保存事件（新手读档、换档、游戏自动保存链）都标记重新同步，
                // 等静默窗口后重建实体表——修复"读档后仍写旧实体"的问题
                _lastMgrCaptureAt = UnityEngine.Time.realtimeSinceStartup;
                _resyncPending = true;
                _resyncAttempts = 0;
            }
            UpdateStatus();
        }
        catch (Exception e) { RawLog("STAFFLIVE PostManager err " + e.Message); }
    }

    private static void PostEntity(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length < 2) return;
            object a0 = __args[0], a1 = __args[1];
            if (a0 == null || a1 == null) return;
            object sd = HasIntProp(a0.GetType(), "CurrentSan") ? a0 : (HasIntProp(a1.GetType(), "CurrentSan") ? a1 : null);
            if (sd == null) return;
            object ent = ReferenceEquals(sd, a0) ? a1 : a0;
            var sdt = sd as SaveLoadSystem.StaffData;
            if (sdt == null) return;
            VotePair(ent, sdt);
            AddPair(ent, sdt);
        }
        catch (Exception e) { RawLog("STAFFLIVE PostEntity err " + e.Message); }
    }

    private static bool HasIntProp(Type t, string name)
    {
        try
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p != null && p.PropertyType == typeof(int);
        }
        catch { return false; }
    }

    // ---------------- 投票 oracle ----------------
    private static void VotePair(object ent, object sd)
    {
        try
        {
            int cur = ReadSdInt(sd, "CurrentSan");
            int max = ReadSdInt(sd, "MaxSan");
            long ts = ReadSdLong(sd, "LastSanUpdateTime");
            if (max <= 0) return;   // 无理智条的特殊角色不参与投票
            _votePairs++;
            foreach (var p in IntProps(ent))
            {
                int v;
                try { v = (int)p.GetValue(ent); } catch { continue; }
                long lv = v;
                if (lv == max) Bump(_maxVotes, p.Name);
                else if (lv * 1000 == max) Bump(_maxScaled, p.Name);
                if (lv == cur) Bump(_curVotes, p.Name);
                else if (lv * 1000 == cur) Bump(_curScaled, p.Name);
            }
            foreach (var p in LongProps(ent))
            {
                long v;
                try { v = (long)p.GetValue(ent); } catch { continue; }
                if (ts != 0 && v == ts) Bump(_tsVotes, p.Name);
            }
        }
        catch { }
    }

    private static void Bump(Dictionary<string, int> d, string k)
    {
        d.TryGetValue(k, out var c);
        d[k] = c + 1;
    }

    private static int ReadSdInt(object sd, string n)
    {
        try { return Convert.ToInt32(sd.GetType().GetProperty(n).GetValue(sd)); } catch { return 0; }
    }

    private static long ReadSdLong(object sd, string n)
    {
        try { return Convert.ToInt64(sd.GetType().GetProperty(n).GetValue(sd)); } catch { return 0; }
    }

    private static bool TryLockFromVotes()
    {
        if (_votePairs < 2) return false;
        int need = Math.Max(2, (int)Math.Ceiling(_votePairs * 0.8));
        bool changed = false;

        if (_maxProp == null)
        {
            string best = null; int bestV = 0; bool scaled = false;
            foreach (var kv in _maxVotes) if (kv.Value > bestV) { bestV = kv.Value; best = kv.Key; }
            foreach (var kv in _maxScaled) if (kv.Value > bestV) { bestV = kv.Value; best = kv.Key; scaled = true; }
            if (best != null && bestV >= need)
            {
                _maxProp = best; _scaledMax = scaled; changed = true;
                RawLog($"STAFFLIVE 锁定上限字段 {_maxProp}" + (scaled ? "(×1000)" : "") + $" 票 {bestV}/{_votePairs}");
            }
        }
        if (_curProp == null && _maxProp != null)
        {
            string best = null; int bestV = 0; bool scaled = false;
            foreach (var kv in _curVotes) { if (kv.Key == _maxProp) continue; if (kv.Value > bestV) { bestV = kv.Value; best = kv.Key; } }
            foreach (var kv in _curScaled) { if (kv.Key == _maxProp) continue; if (kv.Value > bestV) { bestV = kv.Value; best = kv.Key; scaled = true; } }
            if (best != null && bestV >= need)
            {
                _curProp = best; _scaledCur = scaled; changed = true;
                RawLog($"STAFFLIVE 锁定理智字段 {_curProp}" + (scaled ? "(×1000)" : "") + $" 票 {bestV}/{_votePairs}");
            }
        }
        if (_tsProp == null)
        {
            string best = null; int bestV = 0;
            foreach (var kv in _tsVotes) if (kv.Value > bestV) { bestV = kv.Value; best = kv.Key; }
            if (best != null && bestV >= need)
            {
                _tsProp = best; changed = true;
                RawLog($"STAFFLIVE 锁定时间戳字段 {_tsProp} 票 {bestV}/{_votePairs}");
            }
        }
        if (changed) UpdateStatus();
        return changed;
    }

    private static void AddPair(object ent, SaveLoadSystem.StaffData sd)
    {
        try
        {
            foreach (var pr in _pairs) if (ReferenceEquals(pr.Ent, ent)) { if (sd != null) pr.Sd = sd; return; }
            int idx = -1;
            if (sd != null)
            {
                var list = StaffList();
                if (list != null)
                {
                    int logic = ReadSdInt(sd, "LogicID");
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && (list[i].LogicID == logic || ReferenceEquals(list[i], sd))) { idx = i; break; }
                }
            }
            if (_pairs.Count < 200) _pairs.Add(new Pair { Ent = ent, Sd = sd, Idx = idx });
            if (_pairs.Count == 1)
                RawLog("STAFFLIVE 实体+存档对已捕获（首对 " + (sd == null ? "(仅实体)" : (sd.Name ?? "?")) + "）");
        }
        catch { }
    }

    // ---------------- Update 轮询状态机 ----------------
    public static void Tick()
    {
        try
        {
            float now = UnityEngine.Time.realtimeSinceStartup;

            // 1) 管理器缺失 → 静态入口（管理器类型继承 Foundation.Module.dfi<T> 的静态属性）
            if (Manager == null && ManagerType != null && now - _lastStaticTry > 3f && _staticTries < 10 && PiperBridge.SaveTreeReady())
            {
                _staticTries++; _lastStaticTry = now;
                TryStaticInstance();
            }

            // 2) 同步：任何载入事件后静默 4 秒 → 重新同步（重建实体表；重新读档后旧实体引用作废）
            if (Manager != null && _resyncPending && !_inProbe && now - _lastMgrCaptureAt > 4f && PiperBridge.SaveTreeReady())
            {
                _resyncPending = false;
                FireSync();
            }

            // 3) 投票够数就锁字段
            if (string.IsNullOrEmpty(_maxProp) || _curProp == null) TryLockFromVotes();

            // 4) 锁不全时跑兜底：max 双射 + 枚举管理器字典
            if (!Ready && _pairs.Count == 0 && Manager != null && _probeFired && !_enumFallbackTried && now - _managerAt > 3f)
            {
                _enumFallbackTried = true;
                var sds = StaffList();
                if (sds != null && sds.Count > 0) EnumerateEntitiesFallback(sds);
            }

            // 5) 锁定后：机制透视 dump（一次）
            if (Ready && !_diagDumped && _pairs.Count > 0) { _diagDumped = true; DumpMechanism(); }

            // 5.5) 调试验证：锁定后自动执行一次「全部回满」并回读（配置门控）
            if (Ready && VerifyOnce && !_verifyDone) { _verifyDone = true; VerifyRestore(); }

            // 5.6) 调试验证：失智复位自测（置失智→复位，配置门控）
            if (Ready && InsanitySelfTest && !_selfTestDone) { _selfTestDone = true; RunInsanitySelfTest(); }

            // 6) 下降快照（机制证据）：锁定理智字段后 60 秒对比
            if (Ready && !_decreaseLogged)
            {
                if (_snapAt < 0) { TakeSnap(); }
                else if (now - _snapAt > 60f) { LogDecrease(); }
            }

            // 7) 时间戳纪元判定
            if (_tsProp != null && !_epochDone) EpochTick(now);

            UpdateStatus();
        }
        catch (Exception e) { RawLog("STAFFLIVE tick err " + e.Message); }
    }

    private static void FireSync()
    {
        if (_inProbe) return;
        _inProbe = true;
        try
        {
            var gse = PiperBridge.GSE();
            var all = gse == null ? null : gse.AllStaffSaveData;
            if (all == null) { _probeFailed = true; RawLog("STAFFLIVE 同步探针: AllStaffSaveData 未就绪"); return; }
            var m = FindMethod(all.GetType(), "SaveAllStaffData", 1);
            if (m == null) { _probeFailed = true; RawLog("STAFFLIVE 同步探针: 未找到 SaveAllStaffData(1) 方法"); return; }

            int before = _pairs.Count;
            _pairs.Clear();   // v4.17.2：旧实体引用全部作废，由 SaveStaffEntity 钩子重新喂入
            _resyncAttempts++;
            // v4.17.2：游戏自己的 Save() 会先清空 StaffList 再调 SaveAllStaffData；我们直调内层
            // 方法必须补上清空，否则每次同步都会往存档树里追加重份（实测 46→92→138…）
            try { var list0 = StaffList(); if (list0 != null) list0.Clear(); }
            catch (Exception ce) { RawLog("STAFFLIVE 清空树 StaffList 失败(继续): " + ce.Message); }
            RawLog("STAFFLIVE 同步探针: 触发 SaveAllStaffData(管理器)…（第 " + _resyncAttempts + " 次）");
            m.Invoke(all, new[] { Manager });
            _probeFired = true; _probeFailed = false;

            var sds = StaffList();
            int treeCount = sds == null ? -1 : sds.Count;
            RawLog($"STAFFLIVE 同步探针: 完成，实体对 {before} → {_pairs.Count}（存档 {treeCount}，投票 {_votePairs} 对）");
            if (treeCount > 0 && _pairs.Count > 0 && _pairs.Count != treeCount && _resyncAttempts < 5)
            {
                // 很可能撞上了加载中途（实体还没建全）——隔一会儿再来一轮，直到实体数与存档一致
                RawLog($"STAFFLIVE 实体数({_pairs.Count})与存档({treeCount})不符，稍后自动重试同步");
                _lastMgrCaptureAt = UnityEngine.Time.realtimeSinceStartup;
                _resyncPending = true;
            }
            else
            {
                // 新一批实体：重跑机制透视/下降快照/时间戳判定
                _diagDumped = false; _decreaseLogged = false; _snapAt = -1; _epochDone = false; _epochAt1 = -1;
                UpdateStatus();
            }
        }
        catch (Exception e)
        {
            _probeFailed = true;
            RawLog("STAFFLIVE 同步探针失败(不影响游戏): " + e.Message);
        }
        finally { _inProbe = false; }
    }

    private static void TryStaticInstance()
    {
        try
        {
            var t = ManagerType;
            while (t != null)
            {
                if (t.IsGenericType && t.Namespace == "Foundation.Module" && t.GetGenericArguments().Length == 1
                    && t.GetGenericArguments()[0] == ManagerType)
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        if (p.PropertyType != ManagerType) continue;
                        try
                        {
                            var v = p.GetValue(null);
                            if (v != null)
                            {
                                Manager = v;
                                _managerAt = UnityEngine.Time.realtimeSinceStartup;
                                _lastMgrCaptureAt = UnityEngine.Time.realtimeSinceStartup;
                                _resyncPending = true;
                                _resyncAttempts = 0;
                                RawLog("STAFFLIVE 静态入口取得管理器: " + t.FullName + "." + p.Name);
                                UpdateStatus();
                                return;
                            }
                        }
                        catch (Exception e)
                        {
                            if (!_staticWarned) { _staticWarned = true; RawLog("STAFFLIVE 静态属性 " + p.Name + " 读取失败: " + e.Message); }
                        }
                    }
                }
                t = t.BaseType;
            }
        }
        catch (Exception e) { RawLog("STAFFLIVE TryStaticInstance err " + e.Message); }
    }

    private static void EpochTick(float now)
    {
        try
        {
            if (_epochAt1 < 0)
            {
                if (_pairs.Count == 0) return;
                _epochSample1 = ReadLong(_pairs[0].Ent, _tsProp);
                _epochAt1 = now;
            }
            else if (now - _epochAt1 > 3f)
            {
                if (_pairs.Count == 0) { _epochDone = true; return; }
                long t2 = ReadLong(_pairs[0].Ent, _tsProp);
                double perMs = (t2 - _epochSample1) / 3000.0;
                // 每毫秒增量经验值：.NET ticks≈10000 / unix毫秒≈1 / 游戏60Hz时钟≈0.06 / unix秒≈0.001
                if (perMs >= 3000) _tsEpoch = "ticks";
                else if (perMs >= 0.3 && perMs <= 3) _tsEpoch = "unixms";
                else if (perMs >= 0.0005 && perMs <= 0.01) _tsEpoch = "unixsec";
                else if (perMs >= 0.02 && perMs <= 0.2)
                {
                    // 游戏时钟 tick（60/秒）：写理智时同步到当前帧，防补扣
                    _tsEpoch = "gameticks";
                    RawLog($"STAFFLIVE 时间戳纪元: 游戏时钟tick(60/s, 回满时同步当前帧)（Δ={t2 - _epochSample1}）");
                    _epochDone = true;
                    return;
                }
                else _tsEpoch = null;
                RawLog($"STAFFLIVE 时间戳纪元: {( _tsEpoch ?? "静止/未识别(不写)")}（Δ={t2 - _epochSample1}）");
                _epochDone = true;
            }
        }
        catch { _epochDone = true; }
    }

    // 兜底枚举：管理器属性里的集合元素（KVP 拆 Value），带完整日志
    private static void EnumerateEntitiesFallback(Il2CppSystem.Collections.Generic.List<SaveLoadSystem.StaffData> sds)
    {
        RawLog("STAFFLIVE 兜底枚举开始（管理器 " + (ManagerType == null ? "?" : ManagerType.Name) + "）");
        try
        {
            foreach (var p in Manager.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object col = null;
                try { col = p.GetValue(Manager); } catch { }
                if (col == null) continue;
                var items = ReadCollection(col);
                if (items.Count == 0) continue;
                var cand = new List<object>();
                foreach (var it in items)
                {
                    var it2 = it;
                    if (it2 == null) continue;
                    if (it2.GetType().Name.StartsWith("KeyValuePair"))
                    {
                        var v = GetProp(it2, "Value");
                        if (v != null) it2 = v;
                    }
                    if (it2 is string || it2.GetType().IsPrimitive) continue;
                    if (IntProps(it2).Length >= 4) cand.Add(it2);
                }
                RawLog($"STAFFLIVE 兜底枚举 {p.Name}: 元素 {items.Count} → 实体候选 {cand.Count}");
                if (cand.Count < 3) continue;
                if (Math.Abs(cand.Count - sds.Count) > sds.Count) continue;
                foreach (var e in cand) AddPair(e, null);
                break;
            }
        }
        catch (Exception e) { RawLog("STAFFLIVE 兜底枚举 err " + e.Message); }
    }

    private static MethodInfo FindMethod(Type t, string name, int paramCount)
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!m.DeclaringType.Equals(t)) continue;
            if (m.Name == name && m.GetParameters().Length == paramCount) return m;
        }
        return null;
    }

    // ---------------- 机制透视 dump ----------------
    public static string DumpMechanismNow()
    {
        DumpMechanism();
        return _pairs.Count == 0 ? "无实体对（先连接）" : $"已 dump {Math.Min(8, _pairs.Count)}/{_pairs.Count} 对到日志（搜 STAFFLIVE DIAG）";
    }

    private static void DumpMechanism()
    {
        try
        {
            RawLog($"STAFFLIVE DIAG ==== 机制透视：{_pairs.Count} 对，锁定 理智={_curProp ?? "?"} 上限={_maxProp ?? "?"} 时间戳={_tsProp ?? "?"} ====");
            int n = Math.Min(8, _pairs.Count);
            for (int i = 0; i < n; i++)
            {
                var pr = _pairs[i];
                var sd = pr.Sd;
                var sb = new System.Text.StringBuilder();
                sb.Append($"DIAG pair#{i} ");
                if (sd != null)
                    sb.Append($"name='{sd.Name}' logic={sd.LogicID} cfg={sd.StaffConfigID} unit={sd.UnitConfigID} san={sd.CurrentSan}/{sd.MaxSan} cost={sd.SanCostPerTick} ts={sd.LastSanUpdateTime}");
                if (pr.Ent != null)
                {
                    sb.Append(" || ent:");
                    foreach (var p in IntProps(pr.Ent))
                    {
                        try { sb.Append(' ').Append(p.Name).Append('=').Append(p.GetValue(pr.Ent)); } catch { }
                    }
                    foreach (var p in EnumProps(pr.Ent))
                    {
                        try { sb.Append(' ').Append(p.Name).Append('=').Append(p.GetValue(pr.Ent)); } catch { }
                    }
                    foreach (var p in LongProps(pr.Ent))
                    {
                        try { sb.Append(' ').Append(p.Name).Append('=').Append(p.GetValue(pr.Ent)); } catch { }
                    }
                }
                var s = sb.ToString();
                for (int k = 0; k < s.Length; k += 900)
                    RawLog("STAFFLIVE " + s.Substring(k, Math.Min(900, s.Length - k)));
            }
        }
        catch (Exception e) { RawLog("STAFFLIVE DIAG err " + e.Message); }
    }

    private static void TakeSnap()
    {
        try
        {
            _snapCur = new Dictionary<object, int>();
            foreach (var pr in _pairs)
                if (pr.Ent != null) _snapCur[pr.Ent] = ReadInt(pr.Ent, _curProp);
            _snapAt = UnityEngine.Time.realtimeSinceStartup;
        }
        catch { _decreaseLogged = true; }
    }

    private static void LogDecrease()
    {
        _decreaseLogged = true;
        try
        {
            int dec = 0, same = 0, inc = 0;
            foreach (var pr in _pairs)
            {
                if (pr.Ent == null) continue;
                int v0;
                if (!_snapCur.TryGetValue(pr.Ent, out v0)) continue;
                int v1 = ReadInt(pr.Ent, _curProp);
                if (v1 < v0) dec++; else if (v1 > v0) inc++; else same++;
            }
            RawLog($"STAFFLIVE DIAG 60秒内 理智字段变化: 下降 {dec} / 不变 {same} / 上升 {inc}（字段 {_curProp}）——下降为主即为理智本尊");
        }
        catch { }
    }

    // ---------------- 属性反射工具 ----------------
    private static PropertyInfo[] IntProps(object o) => Props(o.GetType(), _intCache, typeof(int));
    private static PropertyInfo[] LongProps(object o) => Props(o.GetType(), _longCache, typeof(long));
    private static PropertyInfo[] EnumProps(object o) => Props(o.GetType(), _enumCache, null, true);

    private static PropertyInfo[] Props(Type t, Dictionary<Type, PropertyInfo[]> cache, Type type, bool enumOnly = false)
    {
        PropertyInfo[] r;
        if (cache.TryGetValue(t, out r)) return r;
        var list = new List<PropertyInfo>();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (p.Name.EndsWith("_k__BackingField")) continue;
            if (enumOnly) { if (!p.PropertyType.IsEnum) continue; }
            else if (p.PropertyType != type) continue;
            if (!p.CanRead) continue;
            list.Add(p);
        }
        r = list.ToArray();
        cache[t] = r;
        return r;
    }

    private static object GetProp(object o, string name)
    {
        try
        {
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null ? null : p.GetValue(o);
        }
        catch { return null; }
    }

    private static int ReadInt(object o, string prop)
    {
        try { return Convert.ToInt32(o.GetType().GetProperty(prop).GetValue(o)); } catch { return 0; }
    }

    private static long ReadLong(object o, string prop)
    {
        try { return Convert.ToInt64(o.GetType().GetProperty(prop).GetValue(o)); } catch { return 0; }
    }

    private static bool WriteInt(object o, string prop, int val)
    {
        try
        {
            var p = o.GetType().GetProperty(prop);
            if (p == null || !p.CanWrite) { RawLog("STAFFLIVE 写入失败: " + prop + " 无 setter"); return false; }
            p.SetValue(o, val);
            return ReadInt(o, prop) == val;
        }
        catch (Exception e) { RawLog("STAFFLIVE 写入 " + prop + " err " + e.Message); return false; }
    }

    private static void WriteTimestamp(object o)
    {
        // v4.17.4：bpoi 是"游戏时钟 tick"（帧计数）——写理智时把时间戳推到当前值，
        // 防止游戏按 (nowTick - bpoi) 补扣把回满吃回去（实测会掉 371 点）。
        if (_tsProp == null || _tsEpoch == null) return;
        try
        {
            var p = o.GetType().GetProperty(_tsProp);
            if (p == null || !p.CanWrite) return;
            long now;
            if (_tsEpoch == "ticks") now = DateTime.UtcNow.Ticks;
            else if (_tsEpoch == "unixms") now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            else if (_tsEpoch == "unixsec") now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            else if (_tsEpoch == "gameticks") now = _tsNowTick;
            else return;
            if (now > 0) p.SetValue(o, now);
        }
        catch { }
    }

    // 回满前抓取"当前游戏帧"：取全员 bpoi 的最大值（正在工作的员工每帧都在刷新它）
    private static long _tsNowTick;
    private static void RefreshNowTick()
    {
        try
        {
            if (_tsProp == null || _pairs.Count == 0) return;
            long mx = 0;
            foreach (var pr in _pairs)
            {
                if (pr.Ent == null) continue;
                long v = ReadLong(pr.Ent, _tsProp);
                if (v > mx) mx = v;
            }
            if (mx > 0) _tsNowTick = mx;
        }
        catch { }
    }

    private static List<object> ReadCollection(object col)
    {
        var res = new List<object>();
        try
        {
            var cntP = col.GetType().GetProperty("Count");
            if (cntP != null && cntP.PropertyType == typeof(int))
            {
                var getM = col.GetType().GetMethod("get_Item", new[] { typeof(int) });
                if (getM != null)
                {
                    int n = (int)cntP.GetValue(col);
                    for (int i = 0; i < n && i < 256; i++)
                    {
                        var v = getM.Invoke(col, new object[] { i });
                        if (v != null) res.Add(v);
                    }
                    return res;
                }
            }
        }
        catch { res.Clear(); }
        try
        {
            var en = col.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(col, null);
            if (en != null)
            {
                var mov = en.GetType().GetMethod("MoveNext", Type.EmptyTypes);
                var curP = en.GetType().GetProperty("Current");
                int steps = 0;
                while (mov != null && curP != null && (bool)mov.Invoke(en, null) && steps++ < 512)
                {
                    var v = curP.GetValue(en);
                    if (v != null) res.Add(v);
                }
            }
        }
        catch { }
        return res;
    }

    private static Il2CppSystem.Collections.Generic.List<SaveLoadSystem.StaffData> StaffList()
    {
        try
        {
            var gse = PiperBridge.GSE();
            var all = gse == null ? null : gse.AllStaffSaveData;
            return all == null ? null : all.StaffList;
        }
        catch { return null; }
    }

    // ---------------- 对外：实时读取 / 写入 ----------------
    public class LiveRow { public string Name; public int Cur; public int Max; public int SdIdx; }

    public static List<LiveRow> ReadRows(out string note)
    {
        var rows = new List<LiveRow>();
        note = "";
        if (Manager != null && (_resyncPending || _pairs.Count == 0)) FireSync();   // 面板刷新时顺带补同步
        if (!Ready)
        {
            note = Manager == null
                ? "运行时未连接（读档进游戏后自动连接，或点[连接运行时]）"
                : (_pairs.Count == 0 ? "同步探针未产出数据（点[连接运行时]重试，看日志 STAFFLIVE）" : "理智字段锁定中（点[连接运行时]或等几秒）");
            return rows;
        }
        foreach (var pr in _pairs)
        {
            if (pr.Ent == null) continue;
            int maxRaw = ReadInt(pr.Ent, _maxProp);
            if (maxRaw <= 0) continue;
            int curRaw = ReadInt(pr.Ent, _curProp);
            // 显示归一：实体存毫单位时 /1000（与游戏内显示一致）；实体本就是显示单位则原样
            int max = _scaledMax ? maxRaw : maxRaw / 1000;
            int cur = _scaledMax ? curRaw : curRaw / 1000;
            if (max <= 0) continue;
            string nm = pr.Sd != null && !string.IsNullOrEmpty(pr.Sd.Name) ? pr.Sd.Name : ("员工#" + rows.Count);
            rows.Add(new LiveRow { Name = nm, Cur = cur, Max = max, SdIdx = pr.Idx });
        }
        return rows;
    }

    public static string SetAllFull()
    {
        if (Manager == null) return NotReadyMsg();
        FireSync();   // v4.17.2：写前强制同步——重读取档后旧实体引用已作废，必须重新拿
        if (!Ready || _pairs.Count == 0) return NotReadyMsg();
        var sdsC = StaffList();
        if (sdsC != null && _pairs.Count != sdsC.Count)
            return $"⚠ 实体数 {_pairs.Count} 与存档 {sdsC.Count} 不一致（已重新同步，请再点一次）";
        try
        {
            RefreshNowTick();   // v4.17.4：先抓当前游戏帧，写理智时同步时间戳防懒结算补扣
            int ok = 0, skip = 0, cured = 0;
            foreach (var pr in _pairs)
            {
                if (pr.Ent == null) { skip++; continue; }
                int max = ReadInt(pr.Ent, _maxProp);
                if (max <= 0) { skip++; continue; }
                if (WriteInt(pr.Ent, _curProp, max)) { WriteTimestamp(pr.Ent); ok++; MirrorTree(pr); }
                if (CureIfInsane(pr.Ent)) cured++;   // v4.17.3：顺带解除失智状态
            }
            RawLog($"STAFFLIVE SetAllFull 实体写入 {ok} / {skip}（{_curProp}={_maxProp}）" + (cured > 0 ? $"，解除失智 {cured} 人" : ""));
            PiperBridge.Toast(cured > 0 ? $"员工理智已实时回满（{ok} 人，解除失智 {cured} 人）" : $"员工理智已实时回满（{ok} 人）");
            return $"✔ {ok} 人理智已实时回满" + (cured > 0 ? $"，{cured} 人失智状态已解除" : "") + "（游戏内立即生效，随下次存档保存）";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    public static string SetRowFull(int sdIdx, int fallbackMax = 0)
    {
        if (Manager == null) return NotReadyMsg();
        FireSync();   // v4.17.2：写前强制同步
        if (!Ready || _pairs.Count == 0) return NotReadyMsg();
        try
        {
            var sds = StaffList();
            if (sds != null && _pairs.Count != sds.Count)
                return $"⚠ 实体数 {_pairs.Count} 与存档 {sds.Count} 不一致（已重新同步，请再点一次）";
            int targetMax;
            string nm;
            if (sds != null && sdIdx >= 0 && sdIdx < sds.Count && sds[sdIdx] != null)
            { targetMax = sds[sdIdx].MaxSan; nm = sds[sdIdx].Name ?? ("#" + sdIdx); }
            else if (fallbackMax > 0) { targetMax = fallbackMax; nm = "员工(上限" + fallbackMax + ")"; }
            else return "⚠ 行索引无效";
            if (targetMax <= 0) return "⚠ 该角色无理智条（MaxSan=0）";
            int ok = 0, cured = 0;
            RefreshNowTick();   // v4.17.4：同步游戏帧时间戳
            foreach (var pr in _pairs)
            {
                if (pr.Ent == null) continue;
                int max = ReadInt(pr.Ent, _maxProp);
                if (max <= 0) continue;
                bool hit = pr.Idx == sdIdx;
                if (!hit && (max == targetMax || max * 1000 == targetMax)) hit = true;
                if (!hit) continue;
                if (WriteInt(pr.Ent, _curProp, max)) { WriteTimestamp(pr.Ent); ok++; MirrorTree(pr); }
                if (CureIfInsane(pr.Ent)) cured++;
            }
            if (ok == 0) return "⚠ 未匹配到该员工的运行时实体（点[连接运行时]后重试）";
            RawLog($"STAFFLIVE SetRowFull sd#{sdIdx} max={targetMax} 写入 {ok} 实体" + (cured > 0 ? $"，解除失智 {cured}" : ""));
            PiperBridge.Toast(nm + " 理智已实时回满" + (cured > 0 ? "，失智已解除" : ""));
            return $"✔ {nm} 理智已实时回满（匹配 {ok} 个实体" + (cured > 0 ? $"，解除失智 {cured}" : "") + "）";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    private static void MirrorTree(Pair pr)
    {
        try { if (pr.Sd != null && pr.Sd.MaxSan > 0) pr.Sd.CurrentSan = pr.Sd.MaxSan; } catch { }
    }

    // ---------- 状态复位（v4.17.3）：失智 Insane → 空闲 Idle ----------
    // 理智是数值、失智是状态（bbj 枚举）；只回满数值不够——失智者仍顶着"失智"标、
    // 不会恢复工作。状态字段名按"枚举含 Insane/Resting/Working/Idle 四成员"运行时发现，跨构建安全。
    public static bool InsanitySelfTest;
    private static bool _selfTestDone;
    private static readonly List<PropertyInfo> _stateProps = new List<PropertyInfo>();
    private static object _insaneVal, _idleVal;

    private static void EnsureStateProps(object ent)
    {
        if (_stateProps.Count > 0 || ent == null) return;
        try
        {
            foreach (var p in ent.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    var et = p.PropertyType;
                    if (!et.IsEnum) continue;
                    bool ins = false, idle = false, work = false, rest = false;
                    foreach (var n in Enum.GetNames(et))
                    {
                        if (n == "Insane") ins = true;
                        else if (n == "Idle") idle = true;
                        else if (n == "Working") work = true;
                        else if (n == "Resting") rest = true;
                    }
                    if (!(ins && idle && work && rest)) continue;
                    _stateProps.Add(p);
                    if (_insaneVal == null)
                    {
                        _insaneVal = Enum.Parse(et, "Insane");
                        _idleVal = Enum.Parse(et, "Idle");
                    }
                }
                catch { }
            }
            var sb = new System.Text.StringBuilder();
            foreach (var p in _stateProps) { sb.Append(p.Name).Append(' '); }
            if (_stateProps.Count > 0)
                RawLog($"STAFFLIVE 状态字段发现: {sb.ToString().Trim()}（Insane={Convert.ToInt32(_insaneVal)} Idle={Convert.ToInt32(_idleVal)}）");
            else
                RawLog("STAFFLIVE 状态字段发现: 未找到 bbj 状态枚举属性");
        }
        catch (Exception e) { RawLog("STAFFLIVE 状态字段发现 err " + e.Message); }
    }

    // 失智 → 空闲；返回 true=本次复位了
    private static bool CureIfInsane(object ent)
    {
        if (ent == null) return false;
        EnsureStateProps(ent);
        bool cured = false;
        foreach (var p in _stateProps)
        {
            try
            {
                var v = p.GetValue(ent);
                if (v == null) continue;
                if (Convert.ToInt32(v) == Convert.ToInt32(_insaneVal))
                {
                    p.SetValue(ent, _idleVal);
                    var v2 = p.GetValue(ent);
                    if (v2 != null && Convert.ToInt32(v2) == Convert.ToInt32(_idleVal)) cured = true;
                }
            }
            catch { }
        }
        return cured;
    }

    private static string ReadStateStr(object ent)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in _stateProps) { try { sb.Append(p.Name).Append('=').Append(p.GetValue(ent)).Append(' '); } catch { } }
        return sb.ToString().Trim();
    }

    private static void RunInsanitySelfTest()
    {
        try
        {
            Pair target = null;
            foreach (var pr in _pairs) { if (pr.Ent != null && ReadInt(pr.Ent, _maxProp) > 0) { target = pr; break; } }
            if (target == null) { RawLog("失智自测: 无可测实体"); return; }
            EnsureStateProps(target.Ent);
            if (_stateProps.Count == 0) { RawLog("失智自测: 无状态字段，跳过"); return; }
            foreach (var p in _stateProps) { try { p.SetValue(target.Ent, _insaneVal); } catch { } }
            RawLog($"失智自测: 已将 {NameOf(target)} 置为 {ReadStateStr(target.Ent)}");
            bool cured = CureIfInsane(target.Ent);
            RawLog("失智自测: 复位" + (cured ? "成功" : "失败") + " → 当前状态 " + ReadStateStr(target.Ent));
            PiperBridge.Toast("失智自测: " + (cured ? "复位成功" : "复位失败"));
        }
        catch (Exception e) { RawLog("失智自测 err " + e.Message); }
    }

    // 调试验证：写前快照 → 全部回满 → 写后回读 → 触发游戏自身同步 → 树回读
    private static void VerifyRestore()
    {
        try
        {
            RawLog("STAFFLIVE 验证 ==== 写前快照 ====");
            foreach (var pr in _pairs)
                if (pr.Ent != null) RawLog($"  前 {NameOf(pr)} {ReadInt(pr.Ent, _curProp)}/{ReadInt(pr.Ent, _maxProp)}");
            var r = SetAllFull();
            RawLog("STAFFLIVE 验证 SetAllFull → " + r);
            foreach (var pr in _pairs)
                if (pr.Ent != null) RawLog($"  后 {NameOf(pr)} {ReadInt(pr.Ent, _curProp)}/{ReadInt(pr.Ent, _maxProp)}");
            // 触发游戏自身 运行时→树 同步：树里出现新值 = 游戏运行时已被改且接受
            FireSync();
            var sds = StaffList();
            if (sds != null)
            {
                RawLog("STAFFLIVE 验证 ==== 存档树回读（应为回满值） ====");
                for (int i = 0; i < sds.Count && i < 60; i++)
                    if (sds[i] != null) RawLog($"  树 {sds[i].Name} {sds[i].CurrentSan}/{sds[i].MaxSan}");
            }
        }
        catch (Exception e) { RawLog("STAFFLIVE 验证 err " + e.Message); }
    }

    private static string NameOf(Pair pr) => pr.Sd != null && !string.IsNullOrEmpty(pr.Sd.Name) ? pr.Sd.Name : "ent";

    private static string NotReadyMsg()
    {
        if (Manager == null) return "⚠ 运行时未连接：读档进游戏后自动连接，或点[连接运行时]（存档树直写会被覆盖，已禁用）";
        if (_pairs.Count == 0) return "⚠ 同步探针未产出数据：点[连接运行时]重试（明细见日志 STAFFLIVE）";
        return "⚠ 理智字段锁定中：点[连接运行时]或等几秒";
    }

    // 手动连接：静态入口 + 立即同步探针 + 锁定，一步到位
    public static string ForceConnect()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            if (Manager == null && ManagerType != null) TryStaticInstance();
            sb.Append(Manager != null ? "管理器✓" : "管理器✗");
            if (Manager != null)
            {
                _resyncPending = false; _resyncAttempts = 0;
                FireSync();
                TryLockFromVotes();
                sb.Append(" 实体对=").Append(_pairs.Count);
            }
            sb.Append(Ready ? " → 已锁(" + FieldInfo + ")" : (_probeFailed ? " → 探针失败(见日志)" : " → 未锁全"));
            UpdateStatus();
            RawLog("STAFFLIVE ForceConnect: " + sb);
            return sb.ToString();
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    private static void UpdateStatus()
    {
        if (Manager == null) { if (Hooked) Status = "未连接（读档进游戏后自动连接，或点[连接运行时]）"; return; }
        if (Ready)
            Status = $"✔ 实时已连接：员工 {_pairs.Count} · 理智 {_curProp} / 上限 {_maxProp}" + (_tsProp != null ? " · 时间戳✓" : "");
        else if (MaxLocked)
            Status = $"已连管理器，上限字段 {_maxProp}，理智字段锁定中…（实体对 {_pairs.Count}）";
        else
            Status = $"已连管理器，字段投票中…（实体对 {_pairs.Count}，投票 {_votePairs}）";
    }
}
