using System;
using System.Collections.Generic;
using UnityEngine;

namespace PiperTrainer;

// v4.9.1 语义定稿：
// - 所有修改都是"绝对值设置"（设为 X），与面板/存档树的旧数字无关，
//   从机制上消灭"基于过期值计算导致覆盖"的问题
// - 不做任何运行时↔存档树的整体同步（LoadAllToList/GenerateSaveFile 实测危险，已移除）
// - 面板显示的数字是"存档基准值"，可能与游戏内实时值有偏差；修改后立即生效
public static class PiperBridge
{
    public static bool FeedbackSelfTest;   // 调试：加钱/加好感/加种子一次并回读校验
    private static bool _feedbackTested;

    public static SaveLoadSystem.GameSaveData GSE()
    {
        try
        {
            var slm = SaveLoadSystem.SaveLoadManager.Instance;
            return slm == null ? null : slm.GameSaveEntity;
        }
        catch { return null; }
    }

    public static void RawLog(string msg)
    {
        try { PiperTrainerPlugin.Log2?.LogInfo(msg); } catch { }
    }

    // ---------- 浮窗提示 ----------
    private struct ToastItem { public string Text; public double Until; }
    private static readonly List<ToastItem> _toasts = new List<ToastItem>();
    private static readonly object _toastLock = new object();

    public static void Toast(string msg)
    {
        lock (_toastLock) { _toasts.Add(new ToastItem { Text = msg, Until = Time.realtimeSinceStartup + 3.0 }); }
    }

    public static void DrawToasts()
    {
        List<ToastItem> alive = null;
        lock (_toastLock)
        {
            double now = Time.realtimeSinceStartup;
            for (int i = _toasts.Count - 1; i >= 0; i--)
                if (_toasts[i].Until < now) _toasts.RemoveAt(i);
            if (_toasts.Count == 0) return;
            alive = new List<ToastItem>(_toasts);
        }
        float ty = 40;
        foreach (var t in alive)
        {
            var content = new GUIContent(t.Text);
            var size = GUI.skin.box.CalcSize(content);
            var r = new Rect((Screen.width - size.x) / 2f - 8, ty, size.x + 16, size.y + 6);
            var oldC = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = oldC;
            GUI.Label(new Rect(r.x + 8, r.y, size.x, size.y), t.Text);
            ty += size.y + 10;
        }
    }

    // ---------- IL2CPP 原生反射：读真实静态字段（托管反射看不到它们） ----------
    private static Il2CppSystem.Object _forced;
    private static bool _il2swept;

    public static Il2CppSystem.Object Forced => _forced;
    public static bool FoundRl => _forced != null;

    public static void SetLiveRl(Il2CppSystem.Object r)
    {
        if (r == null || _forced == r) return;
        _forced = r;
        RawLog("LIVE rl 已连接");
    }

    private static int _sweepTries;

    public static string Il2cppSweep()
    {
        if (_il2swept) return "已扫过";
        if (_sweepTries > 20) return "放弃（多次未找到）";
        _sweepTries++;
        var sb = new System.Text.StringBuilder("rl:");
        // v4.17.5：游戏更新后混淆类型名整体变化（旧 Piper.rl 已不存在），
        // 这里不再按编译期类型名扫描静态字段；实例由 SaveAllStaffData 钩子直接捕获。
        sb.Append("类型名随版本变化，跳过静态字段扫描");

        try
        {
            var t2 = Il2CppInterop.Runtime.Il2CppType.Of<SaveLoadSystem.SaveLoadManager>();
            var fs2 = t2.GetFields(Il2CppSystem.Reflection.BindingFlags.Static
                | Il2CppSystem.Reflection.BindingFlags.Public | Il2CppSystem.Reflection.BindingFlags.NonPublic);
            if (fs2 != null)
                for (int i = 0; i < fs2.Length; i++)
                {
                    try
                    {
                        var v = fs2[i].GetValue(null);
                        RawLog("IL2SWEEP SLM." + fs2[i].Name + " => " + (v == null ? "null" : v.ToString()));
                    }
                    catch { }
                }
        }
        catch (Exception e) { RawLog("IL2SWEEP SLM ERR " + e.Message); }
        RawLog("IL2SWEEP 完成: " + sb);
        return sb.ToString();
    }

    // ---------- 金币/属性（绝对值；金币 901 内部单位=分） ----------
    public class AttrRow { public long AttributeId; public long Value; }

    public static List<AttrRow> GetAttributes(out string note)
    {
        var list = new List<AttrRow>();
        note = "";
        var gse = GSE();
        if (gse == null) { note = "GameSaveEntity 未就绪"; return list; }
        try
        {
            var attrs = gse.AllAttributeSaveData;
            try { attrs.Save(); } catch (Exception e) { note = "Save()同步失败(显示载入值): " + e.Message; }
            var ps = attrs.AttributeParams;
            if (ps == null) { note = "AttributeParams 为空"; return list; }
            for (int i = 0; i < ps.Count; i++)
            {
                var a = ps[i];
                if (a == null) continue;
                list.Add(new AttrRow { AttributeId = a.AttributeId, Value = a.Value });
            }
        }
        catch (Exception e) { note = "读取异常: " + e.Message; }
        return list;
    }

    // 设为绝对值
    public static string SetAttribute(long attributeId, long target)
    {
        var gse = GSE();
        if (gse == null) return "⚠ GameSaveEntity 未就绪（读档进入游戏后再试）";
        try
        {
            var attrs = gse.AllAttributeSaveData;
            var ps = attrs.AttributeParams;
            if (ps == null) return "⚠ AttributeParams 为空";
            int hit = 0;
            for (int i = 0; i < ps.Count; i++)
            {
                var a = ps[i];
                if (a != null && a.AttributeId == attributeId) { a.Value = target; hit++; }
            }
            if (hit == 0) return "⚠ 未找到 AttributeId=" + attributeId;
            try { attrs.Load(); } catch (Exception e) { return "已改写但 Load() 异常: " + e.Message; }
            RawLog($"SetAttribute {attributeId} -> {target} x{hit}");
            string nm = attributeId == 901 ? "金币" : "属性" + attributeId;
            Toast($"{nm} 设为 {target / (attributeId == 901 ? 1000 : 1):N0}");
            return $"✔ {nm} → {target / (attributeId == 901 ? 1000 : 1):N0}";
        }
        catch (Exception e)
        {
            RawLog("SetAttribute ERR " + e);
            return "✘ " + e.Message;
        }
    }

    // ---------- 背包 ----------
    public class ItemRow { public int ConfigId; public int Count; public long Timestamp; public int Idx; public int LogicId; }

    private static Dictionary<int, int> _origCounts;   // 会话快照（还原用）

    private static void EnsureOrigins(SaveLoadSystem.BagData bag)
    {
        if (_origCounts != null) return;
        _origCounts = new Dictionary<int, int>();
        try
        {
            var items = bag.ItemDataList;
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it != null) _origCounts[it.ItemLogicID] = it.Count;
            }
        }
        catch { }
    }

    private static SaveLoadSystem.ItemData FindByLogic(SaveLoadSystem.BagData bag, int logicId)
    {
        var items = bag.ItemDataList;
        if (items == null) return null;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it != null && it.ItemLogicID == logicId) return it;
        }
        return null;
    }

    // 设为绝对数量并回读校验；游戏拒绝/改写则自动还原
    private static string SafeSetCount(SaveLoadSystem.BagData bag, SaveLoadSystem.ItemData it, int target)
    {
        if (it == null) return "⚠ 条目为空";
        int before = it.Count;
        int logic = it.ItemLogicID;
        if (target == before) return "· " + it.ConfigId + " 数量未变化（" + before + "）";
        it.Count = target;
        try { bag.Load(); }
        catch (Exception e) { it.Count = before; try { bag.Load(); } catch { } return "✘ Load() 异常已还原: " + e.Message; }
        try
        {
            var it2 = FindByLogic(bag, logic);
            if (it2 == null) { it.Count = before; try { bag.Load(); } catch { } return "⚠ 回读为空，已还原"; }
            if (it2.Count != target)
            {
                it2.Count = before;
                try { bag.Load(); } catch { }
                return "⚠ 该物品不接受 " + target + "（游戏回写为 " + it2.Count + "），已还原为 " + before;
            }
        }
        catch (Exception e) { return "⚠ 回读异常: " + e.Message; }
        return null;
    }

    public static List<ItemRow> GetItems(out string note)
    {
        var list = new List<ItemRow>();
        note = "";
        // v4.13.1：实时模式（rl.boro）已删——混淆名跨构建漂移，直连成员是 JIT 地雷；
        // 存档树路径在两个构建上都可用
        try
        {
            var gse = GSE();
            if (gse == null) { note = "GameSaveEntity 未就绪"; return list; }
            var bag = gse.BagSaveData;
            EnsureOrigins(bag);
            var items = bag.ItemDataList;
            if (items == null) { note = "ItemDataList 为空"; return list; }
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null) continue;
                list.Add(new ItemRow { Idx = i, ConfigId = it.ConfigId, Count = it.Count, Timestamp = it.Timestamp, LogicId = it.ItemLogicID });
            }
        }
        catch (Exception e) { note = "读取异常: " + e.Message; }
        return list;
    }

    public static string SetItemCount(int logicId, int target)
    {
        // v4.13.1：实时模式已删（见 GetItems 注释）
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        try
        {
            var bag = gse.BagSaveData;
            EnsureOrigins(bag);
            var it = FindByLogic(bag, logicId);
            if (it == null) return "⚠ 未找到该物品（新物品请点背包页[刷新]后再试）";
            int t2 = Math.Min(9999999, Math.Max(0, target));
            var r = SafeSetCount(bag, it, t2);
            var now = FindByLogic(bag, logicId);
            int nowC = now == null ? -1 : now.Count;
            if (r == null)
            {
                Toast("物品 " + it.ConfigId + " 数量 → " + nowC);
                return "✔ 物品 " + it.ConfigId + " 数量 → " + nowC;
            }
            Toast(r);
            return r;
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    public static string RestoreItemCount(int logicId)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        if (_origCounts == null || !_origCounts.ContainsKey(logicId)) return "⚠ 本会话没有该物品的快照";
        try
        {
            var bag = gse.BagSaveData;
            var it = FindByLogic(bag, logicId);
            if (it == null) return "⚠ 未找到该物品";
            it.Count = _origCounts[logicId];
            bag.Load();
            RawLog("RestoreItemCount logic=" + logicId + " -> " + it.Count);
            return "✔ 已还原为 " + it.Count;
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // 复制不可堆叠物品（追加新条目）
    public static string CloneItem(int logicId, int copies)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        try
        {
            var bag = gse.BagSaveData;
            EnsureOrigins(bag);
            var items = bag.ItemDataList;
            var src = FindByLogic(bag, logicId);
            if (src == null) return "⚠ 未找到该物品";
            if (copies < 1) copies = 1;
            if (copies > 20) copies = 20;
            long maxLogic = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var e = items[i];
                if (e != null && e.ItemLogicID > maxLogic) maxLogic = e.ItemLogicID;
            }
            int added = 0;
            for (int n = 0; n < copies; n++)
            {
                var nd = new SaveLoadSystem.ItemData();
                nd.ConfigId = src.ConfigId;
                nd.Count = 1;
                nd.Timestamp = src.Timestamp;
                nd.SeedLogicID = src.SeedLogicID;
                nd.OriginalSeedItemID = src.OriginalSeedItemID;
                nd.ItemLogicID = (int)(maxLogic + 1 + n);
                items.Add(nd);
                added++;
            }
            try { bag.Load(); }
            catch (Exception e)
            {
                try { for (int n = 0; n < added; n++) items.RemoveAt(items.Count - 1); bag.Load(); } catch { }
                return "✘ Load() 异常已回滚: " + e.Message;
            }
            int cnt = 0;
            var items2 = bag.ItemDataList;
            for (int i = 0; i < items2.Count; i++)
            {
                var e2 = items2[i];
                if (e2 != null && e2.ConfigId == src.ConfigId) cnt++;
            }
            if (cnt <= 1 && added > 0)
            {
                try { for (int n = 0; n < added && items2.Count > 0; n++) items2.RemoveAt(items2.Count - 1); bag.Load(); } catch { }
                return "⚠ 游戏拒绝了新条目，已回滚";
            }
            RawLog("CloneItem cfg=" + src.ConfigId + " +" + added);
            Toast("物品 " + src.ConfigId + " 复制 +" + added);
            return "✔ 已复制 " + added + " 份（该物品现有 " + cnt + " 格）";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // 全部已持有物品设为步长值（特殊物品拒绝会自动还原）
    public static string SetAllItems(int target)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        try
        {
            var bag = gse.BagSaveData;
            EnsureOrigins(bag);
            var items = bag.ItemDataList;
            if (items == null) return "⚠ ItemDataList 为空";
            int n = 0, skip = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null) continue;
                int t2 = Math.Min(9999999, Math.Max(0, target));
                if (t2 == it.Count) continue;
                var r = SafeSetCount(bag, it, t2);
                var it2 = FindByLogic(bag, it.ItemLogicID);
                int now = it2 == null ? 0 : it2.Count;
                if (now == t2) { n++; Toast("物品 " + it.ConfigId + " → " + now); }
                else skip++;
            }
            return "✔ " + n + " 种物品已设为 " + target + "，" + skip + " 种被游戏拒绝并已还原";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // 添加全新物品（图鉴可见但未持有的）：由配置创建新条目
    public static string AddNewItem(int cfgId, int count)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        if (count < 1) count = 1;
        try
        {
            var bag = gse.BagSaveData;
            int before = 0, maxLogic = 0;
            var items0 = bag.ItemDataList;
            for (int i = 0; i < items0.Count; i++)
            {
                var e = items0[i];
                if (e == null) continue;
                if (e.ConfigId == cfgId) before += e.Count;
                if (e.ItemLogicID > maxLogic) maxLogic = e.ItemLogicID;
            }
            var nd = new SaveLoadSystem.ItemData();
            nd.ConfigId = cfgId;
            nd.Count = count;
            nd.Timestamp = 0;
            nd.SeedLogicID = 0;
            nd.OriginalSeedItemID = -1;
            nd.ItemLogicID = (int)(maxLogic + 1);
            // v4.17.7：种子必须同时写入 AllSeedSaveData，否则只有背包条目、种不下去
            string seedKind = AttachSeedData(cfgId, nd.ItemLogicID, nd);
            items0.Add(nd);
            try { bag.Load(); }
            catch (Exception e)
            {
                try { gse.BagSaveData.ItemDataList.RemoveAt(gse.BagSaveData.ItemDataList.Count - 1); gse.BagSaveData.Load(); } catch { }
                RemoveLastSeed(seedKind);
                return "✘ Load() 异常已回滚: " + e.Message;
            }
            int after = 0;
            var items1 = gse.BagSaveData.ItemDataList;
            for (int i = 0; i < items1.Count; i++)
            {
                var e2 = items1[i];
                if (e2 != null && e2.ConfigId == cfgId) after += e2.Count;
            }
            if (after >= before + count)
            {
                RawLog("AddNewItem cfg=" + cfgId + " +" + count + " (总" + after + ")");
                Toast(PiperNames.ItemName(cfgId) + " +" + count + "（现有 " + after + "）");
                return "✔ 已添加 " + PiperNames.ItemName(cfgId) + " ×" + count;
            }
            try { items1.RemoveAt(items1.Count - 1); gse.BagSaveData.Load(); } catch { }
            RemoveLastSeed(seedKind);
            if (after > before) return "⚠ 部分生效：现有 " + after + "（原 " + before + "）";
            return "⚠ 游戏拒绝了该物品，已回滚";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // v4.17.7：种子关联数据。背包条目只是"外壳"，种植时游戏按 ItemData.SeedLogicID
    // 去 AllSeedSaveData 找 SeedSaveData；只加外壳就是"能看见但不能用"。
    private static string AttachSeedData(int cfgId, int itemLogic, SaveLoadSystem.ItemData nd)
    {
        try
        {
            if (!PiperNames.ItemName(cfgId).Contains("种子")) return null;
            var gse = GSE();
            var sd = gse == null ? null : gse.AllSeedSaveData;
            if (sd == null) return null;

            int maxLogic = 0;
            var crop = sd.SeedSaveDataList;
            if (crop != null) for (int i = 0; i < crop.Count; i++) { var x = crop[i]; if (x != null && x.SeedLogicID > maxLogic) maxLogic = x.SeedLogicID; }
            var hyb = sd.HybridSeedSaveDataList;
            if (hyb != null) for (int i = 0; i < hyb.Count; i++) { var x = hyb[i]; var d = x == null ? null : x.Data; if (d != null && d.SeedLogicID > maxLogic) maxLogic = d.SeedLogicID; }
            var stf = sd.StaffSeedSaveDataList;
            if (stf != null) for (int i = 0; i < stf.Count; i++) { var x = stf[i]; var d = x == null ? null : x.Data; if (d != null && d.SeedLogicID > maxLogic) maxLogic = d.SeedLogicID; }
            int logic = maxLogic + 1;

            if (cfgId >= 32000)   // 人柴种子
            {
                if (sd.StaffSeedSaveDataList == null) return null;
                var rec = new SaveLoadSystem.StaffSeedSaveData();
                rec.Data = new SaveLoadSystem.SeedSaveData { SeedLogicID = logic, SeedConfigID = cfgId, ItemLogicID = itemLogic };
                rec.ResultStaffConfigIDQueue = new Il2CppSystem.Collections.Generic.List<int>();
                sd.StaffSeedSaveDataList.Add(rec);
                nd.SeedLogicID = logic;
                try { sd.Load(); } catch { }
                RawLog($"AddSeedData staff cfg={cfgId} seedLogic={logic} itemLogic={itemLogic}");
                return "staff";
            }
            if (cfgId >= 31000)   // 杂交种子（基因列表留空，游戏按默认处理）
            {
                if (sd.HybridSeedSaveDataList == null) return null;
                var rec = new SaveLoadSystem.HybridSeedSaveData();
                rec.Data = new SaveLoadSystem.SeedSaveData { SeedLogicID = logic, SeedConfigID = cfgId, ItemLogicID = itemLogic };
                rec.GrowthTime = 0;
                rec.OriginalSeedItemID = -1;
                rec.GeneList = new Il2CppSystem.Collections.Generic.List<int>();
                sd.HybridSeedSaveDataList.Add(rec);
                nd.SeedLogicID = logic;
                try { sd.Load(); } catch { }
                RawLog($"AddSeedData hybrid cfg={cfgId} seedLogic={logic} itemLogic={itemLogic}");
                return "hybrid";
            }
            if (sd.SeedSaveDataList == null) return null;
            var cr = new SaveLoadSystem.SeedSaveData { SeedLogicID = logic, SeedConfigID = cfgId, ItemLogicID = itemLogic };
            sd.SeedSaveDataList.Add(cr);
            nd.SeedLogicID = logic;
            try { sd.Load(); } catch { }
            RawLog($"AddSeedData crop cfg={cfgId} seedLogic={logic} itemLogic={itemLogic}");
            return "crop";
        }
        catch (Exception e) { RawLog("AddSeedData err " + e.Message); return null; }
    }

    private static void RemoveLastSeed(string kind)
    {
        try
        {
            if (string.IsNullOrEmpty(kind)) return;
            var gse = GSE();
            var sd = gse == null ? null : gse.AllSeedSaveData;
            if (sd == null) return;
            if (kind == "staff" && sd.StaffSeedSaveDataList != null && sd.StaffSeedSaveDataList.Count > 0)
                sd.StaffSeedSaveDataList.RemoveAt(sd.StaffSeedSaveDataList.Count - 1);
            else if (kind == "hybrid" && sd.HybridSeedSaveDataList != null && sd.HybridSeedSaveDataList.Count > 0)
                sd.HybridSeedSaveDataList.RemoveAt(sd.HybridSeedSaveDataList.Count - 1);
            else if (kind == "crop" && sd.SeedSaveDataList != null && sd.SeedSaveDataList.Count > 0)
                sd.SeedSaveDataList.RemoveAt(sd.SeedSaveDataList.Count - 1);
            try { sd.Load(); } catch { }
            RawLog("AddSeedData rollback " + kind);
        }
        catch { }
    }

    // ---------- 势力名对照（经验条/唯一等级实锤+暂定） ----------
    public static readonly Dictionary<int, string> FactionNames = new Dictionary<int, string>
    {
        { 700, "商业街" }, { 701, "黑曼卫队" }, { 702, "女神信徒?" }, { 703, "银钥?" },
        { 704, "魔女信徒" }, { 705, "母亲" }, { 706, "钻石海" }
    };
    public static string FactionName(int id) => FactionNames.TryGetValue(id, out var n) ? n : "势力" + id;

    // ---------- 结构探测（PROBE2）：一次跑完 v4.13 所需的全部运行时验证 ----------
    // 依据《修改器问题分析_代码依据.md》的待验证清单；只读不写，全部输出进日志
    public static bool DumpStructures(bool auto)
    {
        try
        {
            RawLog("===== PROBE2 结构探测 开始（" + (auto ? "自动" : "手动") + "）=====");
            var gse = GSE();
            if (gse == null || gse.BagSaveData == null)
            { RawLog("PROBE2 存档树未就绪（GSE=" + (gse != null) + "），稍后自动重试"); return false; }

            ProbeLxEnum();
            var ownedBuildings = ProbeBagBuilding(gse);
            ProbeBuildingConfigs(ownedBuildings);
            ProbeIllustrationSave(gse);
            ProbePlayerFeedback(gse);  // v4.17.7：属性(金币)/好感/种子的真实运行时数据
            if (FeedbackSelfTest && !_feedbackTested) { _feedbackTested = true; RunFeedbackSelfTest(gse); }
            ProbeRuntimeReachable();   // ev/buo/bjg/rl 实例可达性（图鉴配置表依赖此结果）
            RawLog("===== PROBE2 结束 =====");
            Toast(auto ? "结构探测完成（自动），明细进日志" : "结构探测完成，明细进日志");
            return true;
        }
        catch (Exception e)
        {
            RawLog("PROBE2 外层异常: " + e);
            Toast("结构探测异常: " + e.Message);
            return false;
        }
    }

    // 存档树是否就绪（GSE 存在且子系统已建）
    public static bool SaveTreeReady()
    {
        try { var g = GSE(); return g != null && g.BagSaveData != null && g.IllustrationSaveData != null; }
        catch { return false; }
    }

    // ---------- 跨构建反射助手（v4.13.1）：混淆成员一律运行时解析，绝不编译期直连 ----------
    private static System.Reflection.PropertyInfo _piCache = null; // 简单缓存不做了，量小

    private static object RProp(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var p = obj.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(obj);
        }
        catch { return null; }
    }

    private static object RPropStatic(Type t, string name)
    {
        try
        {
            var p = t.GetProperty(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return p == null ? null : p.GetValue(null);
        }
        catch { return null; }
    }

    private static object RCall(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var m = obj.GetType().GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null) return null;
            return m.IsStatic ? m.Invoke(null, null) : m.Invoke(obj, null);
        }
        catch { return null; }
    }

    private static int RCount(object il2List)
    {
        var v = RProp(il2List, "Count");
        try { return v == null ? -1 : Convert.ToInt32(v); } catch { return -1; }
    }

    private static object RAt(object il2List, int i)
    {
        if (il2List == null) return null;
        try
        {
            var m = il2List.GetType().GetMethod("get_Item", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, new[] { typeof(int) }, null);
            return m == null ? null : m.Invoke(il2List, new object[] { i });
        }
        catch { return null; }
    }

    private static int RInt(object obj, string name)
    {
        var v = RProp(obj, name);
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    // 建筑配置表（跨构建双链路，均为 DumpTool 实证）：
    //   学习版：fl.chkf(或 fvl())→fvn()→jw.bnch；字段 bnbm=ID、bnbo=名称
    //   Steam版：fn.chzy(或 fwl())→bmvi(静态)→jy.bnmm；字段 bnlr=ID、bnlt=名称（混淆名整体平移）
    private static object _bldTableCache;
    private static bool _bldTableTried;
    private static object GetBuildingTable()
    {
        if (_bldTableTried) return _bldTableCache;
        _bldTableTried = true;
        try
        {
            // v4.17.5：用未混淆类型取 Assembly-CSharp（旧 Piper.rl 已随版本改名）
            var asm = typeof(SaveLoadSystem.AllStaffData).Assembly;
            // 链路0（2026-10-21 新版）：fn.get_bmzt / fn.fxa() → Example.jz.bnqv
            var fnT0 = asm.GetType("fn");
            if (fnT0 != null)
            {
                object holder0 = RPropStatic(fnT0, "bmzt");
                if (holder0 == null) holder0 = RCallStatic(fnT0, "fxa");
                var table0 = holder0 == null ? null : RProp(holder0, "bnqv");
                if (table0 != null) { _bldTableCache = table0; return table0; }
            }
            // 链路1：学习版 fl
            var flT = asm.GetType("fl");
            if (flT != null)
            {
                object inst = RPropStatic(flT, "chkf");
                if (inst == null) inst = RCallStatic(flT, "fvl");
                var holder = inst == null ? null : RCall(inst, "fvn");
                var table = holder == null ? null : RProp(holder, "bnch");
                if (table != null) { _bldTableCache = table; return table; }
            }
            // 链路2：Steam版 fn（fl 在该构建是别的仓库，fn 才是建筑仓库）
            var fnT = asm.GetType("fn");
            if (fnT != null)
            {
                object holder2 = RPropStatic(fnT, "bmvi");
                if (holder2 == null)
                {
                    object inst2 = RPropStatic(fnT, "chzy");
                    if (inst2 == null) inst2 = RCallStatic(fnT, "fwl");
                    holder2 = inst2 == null ? null : RCall(inst2, "fwh");
                }
                var table2 = holder2 == null ? null : RProp(holder2, "bnmm");
                if (table2 != null) { _bldTableCache = table2; return table2; }
            }
        }
        catch { }
        return null;
    }

    // 跨构建字段名候选（按上面的链路顺序对应）
    private static int BldId(object cfg)
    {
        int v = RInt(cfg, "bnqb");                 // 2026-10-21 新版
        if (v > 0) return v;
        v = RInt(cfg, "bnbm"); if (v > 0) return v; // 学习版
        return RInt(cfg, "bnlr");                   // 旧 Steam 版
    }
    private static string BldName(object cfg)
    {
        // 2026-10-21 新版：名称变成文本 ID（bnqc），用游戏文本解析器取中文名
        int tid = RInt(cfg, "bnqc");
        if (tid > 0)
        {
            var t = PiperNames.ResolveText(tid);
            if (!string.IsNullOrEmpty(t)) return t;
        }
        // 旧版：名称就是字符串字段
        var s = RProp(cfg, "bnbo") as string;
        if (string.IsNullOrEmpty(s)) s = RProp(cfg, "bnlt") as string;
        return s;
    }

    private static object RCallStatic(Type t, string name)
    {
        try
        {
            var m = t.GetMethod(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            return m == null ? null : m.Invoke(null, null);
        }
        catch { return null; }
    }

    // ---------- 建筑/家具（v4.13：写入拥有列表，游戏内建筑/装饰菜单立即可见） ----------
    // 存档实证（AUTO_SAVE_PIPER_2 离线解析）：OwnedBuildingConfigIDList 存 BuildingConfig.bnbm（200-566），
    // BuildingDataList 条目 = {ConfigId=bnbm, Count=堆叠, LogicID=两列表共用的单调递增ID}
    public class BuildingRow { public int Id; public string Name; public bool Owned; }

    public static List<BuildingRow> GetBuildings(out string note)
    {
        var list = new List<BuildingRow>();
        note = "";
        var cfgs = GetBuildingTable();
        if (cfgs == null)
        {
            note = "此游戏构建的建筑表符号不匹配：列表不可用（[图鉴全部解锁]与[获得]存档写入不受影响，可直接用下方存档ID模式）";
            return list;
        }
        var owned = new HashSet<int>();
        try
        {
            var gse = GSE();
            var ol = gse == null ? null : gse.BagSaveData.OwnedBuildingConfigIDList;
            if (ol != null) for (int i = 0; i < ol.Count; i++) owned.Add(ol[i]);
        }
        catch { }
        int n = RCount(cfgs);
        for (int i = 0; i < n; i++)
        {
            var c = RAt(cfgs, i);
            if (c == null) continue;
            int id = BldId(c);
            if (id <= 0) continue;
            var nmObj = BldName(c);
            string nm = string.IsNullOrEmpty(nmObj) ? ("建筑" + id) : nmObj;
            list.Add(new BuildingRow { Id = id, Name = nm, Owned = owned.Contains(id) });
        }
        return list;
    }

    public static string AddBuilding(int id)
    {
        var gse = GSE();
        if (gse == null || gse.BagSaveData == null) return "⚠ 存档树未就绪（读档进游戏后再试）";
        try
        {
            var bag = gse.BagSaveData;
            var ol = bag.OwnedBuildingConfigIDList;
            var bdl = bag.BuildingDataList;
            if (ol == null) return "⚠ OwnedBuildingConfigIDList 为空";

            // LogicID：两个列表共用一个递增序列（存档实证 732/738），取两边最大值+1
            int maxLogic = 0;
            var items = bag.ItemDataList;
            if (items != null) for (int i = 0; i < items.Count; i++) { var e = items[i]; if (e != null && e.ItemLogicID > maxLogic) maxLogic = e.ItemLogicID; }
            if (bdl != null) for (int i = 0; i < bdl.Count; i++) { var e = bdl[i]; if (e != null && e.ItemLogicID > maxLogic) maxLogic = e.ItemLogicID; }

            bool inOwned = false;
            for (int i = 0; i < ol.Count; i++) if (ol[i] == id) { inOwned = true; break; }
            if (!inOwned) ol.Add(id);

            bool inData = false;
            if (bdl != null)
            {
                for (int i = 0; i < bdl.Count; i++) { var e = bdl[i]; if (e != null && e.ConfigId == id) { inData = true; break; } }
                if (!inData)
                {
                    var nd = new SaveLoadSystem.ItemData();
                    nd.ConfigId = id; nd.Count = 1; nd.Timestamp = 0;
                    nd.SeedLogicID = 0; nd.OriginalSeedItemID = 0; nd.ItemLogicID = maxLogic + 1;
                    bdl.Add(nd);
                }
            }
            try { bag.Load(); }
            catch (Exception e) { return "已写入但 Load() 异常（存档已记，重读档生效）: " + e.Message; }

            // 回读校验
            var ol2 = gse.BagSaveData.OwnedBuildingConfigIDList;
            bool ok = false;
            for (int i = 0; i < ol2.Count; i++) if (ol2[i] == id) { ok = true; break; }
            RawLog("AddBuilding " + id + (ok ? " OK" : " 回读失败"));
            Toast(ok ? "建筑 " + id + " 已获得（建筑/装饰菜单可摆）" : "建筑 " + id + " 写入后未保留");
            return ok ? "✔ 建筑 " + id + " 已获得（建筑/装饰菜单可摆）" : "⚠ 游戏未保留该建筑";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // 减少建筑数量（v4.14）：减到 0 视为销毁（条目+拥有ID一并移除）
    public static string ReduceBuilding(int id, int delta)
    {
        if (delta <= 0) return "⚠ 步长无效";
        var gse = GSE();
        if (gse == null || gse.BagSaveData == null) return "⚠ 存档树未就绪（读档进游戏后再试）";
        try
        {
            var bag = gse.BagSaveData;
            var bdl = bag.BuildingDataList;
            if (bdl == null) return "⚠ BuildingDataList 为空";
            int idx = -1, cnt = 0;
            for (int i = 0; i < bdl.Count; i++)
            {
                var e = bdl[i];
                if (e != null && e.ConfigId == id) { idx = i; cnt = e.Count; break; }
            }
            if (idx < 0) return "⚠ 未找到该建筑的持有条目（可能只登记未入库）";
            if (cnt - delta <= 0) return DestroyBuilding(id);
            bdl[idx].Count = cnt - delta;
            try { bag.Load(); }
            catch (Exception e) { return "已写入但 Load() 异常（存档已记，重读档生效）: " + e.Message; }
            RawLog("ReduceBuilding " + id + " " + cnt + "->" + (cnt - delta));
            Toast("建筑 " + id + " 数量 " + cnt + " → " + (cnt - delta));
            return "✔ 建筑 " + id + " 数量 " + cnt + " → " + (cnt - delta);
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // 销毁建筑（v4.14）：BuildingDataList 条目 + OwnedBuildingConfigIDList 登记一并移除；
    // 注意：已摆放在地图上的建筑实例归地图存档管，此操作不回收已摆放的
    public static string DestroyBuilding(int id)
    {
        var gse = GSE();
        if (gse == null || gse.BagSaveData == null) return "⚠ 存档树未就绪（读档进游戏后再试）";
        try
        {
            var bag = gse.BagSaveData;
            var bdl = bag.BuildingDataList;
            var ol = bag.OwnedBuildingConfigIDList;
            int removedData = 0;
            if (bdl != null)
                for (int i = bdl.Count - 1; i >= 0; i--)
                {
                    var e = bdl[i];
                    if (e != null && e.ConfigId == id) { bdl.RemoveAt(i); removedData++; }
                }
            int removedOwned = 0;
            if (ol != null)
                for (int i = ol.Count - 1; i >= 0; i--)
                    if (ol[i] == id) { ol.RemoveAt(i); removedOwned++; }
            if (removedData == 0 && removedOwned == 0) return "· 该建筑本就未拥有";
            try { bag.Load(); }
            catch (Exception e) { return "已写入但 Load() 异常（存档已记，重读档生效）: " + e.Message; }
            // 回读校验
            bool gone = true;
            var ol2 = gse.BagSaveData.OwnedBuildingConfigIDList;
            for (int i = 0; i < ol2.Count; i++) if (ol2[i] == id) { gone = false; break; }
            RawLog("DestroyBuilding " + id + " data-" + removedData + " owned-" + removedOwned + (gone ? " OK" : " 回读仍在"));
            Toast(gone ? "建筑 " + id + " 已销毁" : "建筑 " + id + " 写入后未生效");
            return gone ? "✔ 建筑 " + id + " 已销毁（已摆放的不受影响）" : "⚠ 游戏未保留该销毁";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // ---------- 图鉴解锁（v4.13） ----------
    // 存档实证：IllustrationFinishedList = 普通图鉴（7000-7025 密集段），
    // IllustrationForceFinishedList = 强制图鉴（7100-7132 密集段）；写满两个百位段
    public static string IllustrationStatus(out int fin, out int force)
    {
        fin = force = -1;
        var gse = GSE();
        if (gse == null || gse.IllustrationSaveData == null) return "未就绪";
        try { fin = gse.IllustrationSaveData.IllustrationFinishedList.Count; force = gse.IllustrationSaveData.IllustrationForceFinishedList.Count; }
        catch { }
        return "";
    }

    public static string UnlockAllIllustrations()
    {
        var gse = GSE();
        if (gse == null || gse.IllustrationSaveData == null) return "⚠ 图鉴数据未就绪（读档进游戏后再试）";
        try
        {
            var ill = gse.IllustrationSaveData;
            var fin = ill.IllustrationFinishedList;
            var force = ill.IllustrationForceFinishedList;
            int added = 0;
            for (int id = 7000; id <= 7099; id++)
            {
                bool has = false;
                for (int i = 0; i < fin.Count; i++) if (fin[i] == id) { has = true; break; }
                if (!has) { fin.Add(id); added++; }
            }
            // v4.17.6：新版图鉴 ID 已用到 72xx（实测 ForceFinished 含 7225-7229），强制段扩到 7299
            for (int id = 7100; id <= 7299; id++)
            {
                bool has = false;
                for (int i = 0; i < force.Count; i++) if (force[i] == id) { has = true; break; }
                if (!has) { force.Add(id); added++; }
            }
            try { ill.Load(); }
            catch (Exception e) { return "已写入但 Load() 异常（存档已记，重读档生效）: " + e.Message; }
            int f2 = gse.IllustrationSaveData.IllustrationFinishedList.Count;
            int g2 = gse.IllustrationSaveData.IllustrationForceFinishedList.Count;
            RawLog("UnlockAllIllustrations +" + added + " -> fin=" + f2 + " force=" + g2);
            Toast($"图鉴解锁完成：普通 {f2} 条 / 强制 {g2} 条");
            return $"✔ 图鉴解锁完成：普通 {f2} 条 / 强制 {g2} 条（新增 {added}）";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // a. 物品类别枚举 Example.lx 名字→数值（钉死类别数字映射）
    private static void ProbeLxEnum()
    {
        try
        {
            foreach (var f in typeof(Example.lx).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
            {
                if (f.Name == "value__") continue;
                try { RawLog("PROBE2 lx " + f.Name + " = " + Convert.ToInt32(f.GetValue(null))); } catch { }
            }
        }
        catch (Exception e) { RawLog("PROBE2 lx 异常: " + e.Message); }
    }

    // b. 家具存档：BuildingDataList 全条目 + OwnedBuildingConfigIDList 全量（返回已拥有建筑ID集合供 c 交叉对照）
    private static HashSet<int> ProbeBagBuilding(SaveLoadSystem.GameSaveData gse)
    {
        var owned = new HashSet<int>();
        SaveLoadSystem.BagData bag = null;
        try { bag = gse.BagSaveData; }
        catch (Exception e) { RawLog("PROBE2 BagSaveData getter 异常: " + e.Message); }
        if (bag == null) { RawLog("PROBE2 BagSaveData 为 null"); return owned; }

        Il2CppSystem.Collections.Generic.List<SaveLoadSystem.ItemData> bdl = null;
        try { bdl = bag.BuildingDataList; }
        catch (Exception e) { RawLog("PROBE2 BuildingDataList getter 异常: " + e.Message); }
        int ic = -1;
        try { if (bag.ItemDataList != null) ic = bag.ItemDataList.Count; } catch { }
        RawLog("PROBE2 Bag.BuildingDataList 条数=" + (bdl == null ? "null" : bdl.Count.ToString()) + "（ItemDataList=" + ic + "）");
        if (bdl != null)
            for (int i = 0; i < bdl.Count && i < 300; i++)
            {
                var d = bdl[i];
                if (d == null) continue;
                RawLog($"PROBE2 BLD[{i}] cfg={d.ConfigId} count={d.Count} ts={d.Timestamp} seedLogic={d.SeedLogicID} origSeed={d.OriginalSeedItemID} logic={d.ItemLogicID}");
            }

        try
        {
            var ol = bag.OwnedBuildingConfigIDList;
            if (ol == null) { RawLog("PROBE2 OwnedBuildingConfigIDList 为 null"); return owned; }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ol.Count; i++)
            {
                owned.Add(ol[i]);
                if (sb.Length < 2000) sb.Append(ol[i]).Append(",");
            }
            RawLog("PROBE2 OwnedBuildingConfigIDList 共" + ol.Count + " 个: " + sb);
        }
        catch (Exception e) { RawLog("PROBE2 owned-list 异常: " + e.Message); }
        return owned;
    }

    // c. 建筑配置全表：fl.fvl().fvn().bnch；int 字段逐个过 eua 找名字段；已拥有的建筑单独全字段输出
    private static void ProbeBuildingConfigs(HashSet<int> owned)
    {
        var list = GetBuildingTable();
        if (list == null) { RawLog("PROBE2 BuildingConfig 表不可达（此构建符号不匹配）"); return; }
        int total = RCount(list);
        RawLog("PROBE2 BuildingConfig 共" + total + " 条");
        for (int i = 0; i < total && i < 3; i++)
        {
            var c0 = RAt(list, i);
            RawLog("PROBE2 BLDNAME id=" + BldId(c0) + " name='" + BldName(c0) + "'");
        }
        for (int i = 0; i < total && i < 20; i++)
            DumpOneBuilding(RAt(list, i));
        int dumped = 0;
        for (int i = 0; i < total && dumped < 30; i++)
        {
            var c = RAt(list, i);
            if (c == null || !owned.Contains(BldId(c))) continue;
            RawLog("PROBE2 --- 已拥有建筑 ---");
            DumpOneBuilding(c);
            dumped++;
        }
    }

    private static void DumpOneBuilding(object c)
    {
        if (c == null) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            string[] intFields = { "bnqb", "bnqc", "bnqg", "bnqj", "bnqk", "bnqs", "bnqt",
                                   "bnbm", "bnbn", "bnbs", "bnbv", "bnbw", "bnce", "bncf", "bnlr", "bnls", "bnlx", "bnma", "bnmb", "bnmj", "bnmk" };
            foreach (var f in intFields) { int v = RInt(c, f); if (v != 0) sb.Append(f).Append('=').Append(v).Append(' '); }
            foreach (var f in new[] { "bnqq", "bnqr", "bnbo", "bncc", "bncd", "bnlt", "bnmh", "bnmi" })
            {
                var sv = RProp(c, f) as string;
                if (!string.IsNullOrEmpty(sv)) sb.Append(f).Append("='").Append(sv).Append("' ");
            }
            foreach (var f in intFields)
            {
                int v = RInt(c, f);
                if (v <= 0) continue;
                var s = PiperNames.ResolveText(v);
                if (!string.IsNullOrEmpty(s)) sb.Append("eua(").Append(f).Append('=').Append(v).Append(")='").Append(s).Append("' ");
            }
            RawLog("PROBE2 BCFG " + sb);
        }
        catch (Exception e) { RawLog("PROBE2 BCFG 异常: " + e.Message); }
    }

    // d. 图鉴存档：完成/强制完成/待定列表（逐属性守卫，上次 NRE 定位不到具体行）
    private static void ProbeIllustrationSave(SaveLoadSystem.GameSaveData gse)
    {
        SaveLoadSystem.IllustrationData ill = null;
        try { ill = gse.IllustrationSaveData; }
        catch (Exception e) { RawLog("PROBE2 IllustrationSaveData getter 异常: " + e.Message); }
        if (ill == null) { RawLog("PROBE2 IllustrationSaveData 为 null"); return; }
        try { RawLog("PROBE2 图鉴 CurrentForceID=" + ill.CurrentIllustrationForceID); } catch (Exception e) { RawLog("PROBE2 图鉴 CurrentForceID 异常: " + e.Message); }
        DumpIntList("图鉴 Finished", ill.IllustrationFinishedList);
        DumpIntList("图鉴 ForceFinished", ill.IllustrationForceFinishedList);
        DumpIntList("图鉴 ForcePending", ill.IllustrationForcePendingList);
    }

    // g. 玩家可见数据：属性（金币）/ 好感 / 种子（v4.17.7 核实玩家反馈用）
    private static void ProbePlayerFeedback(SaveLoadSystem.GameSaveData gse)
    {
        try
        {
            var attrs = gse.AllAttributeSaveData;
            if (attrs != null && attrs.AttributeParams != null)
                for (int i = 0; i < attrs.AttributeParams.Count; i++)
                {
                    var a = attrs.AttributeParams[i];
                    if (a != null) RawLog($"PROBE2 ATTR id={a.AttributeId} value={a.Value}");
                }
        }
        catch (Exception e) { RawLog("PROBE2 ATTR err " + e.Message); }

        try
        {
            var npcs = gse.AllFavorData == null ? null : gse.AllFavorData.allNPCData;
            if (npcs != null)
                for (int i = 0; i < npcs.Count; i++)
                {
                    var n = npcs[i];
                    if (n == null) continue;
                    RawLog($"PROBE2 FAVOR npc={n.npcID} value={n.favorValue} lvl={PiperNames.FavorLevel(n.npcID, n.favorValue)} name='{PiperNames.NpcName(n.npcID)}'");
                }
        }
        catch (Exception e) { RawLog("PROBE2 FAVOR err " + e.Message); }

        // 好感配置：找等级阈值/上限（玩家反馈"好感度显示不对"）
        try
        {
            var list = PiperNames.FavorConfigList();
            int cnt = RCount(list);
            RawLog("PROBE2 FAVORCFG count=" + cnt);
            for (int i = 0; i < cnt && i < 2; i++)
            {
                var c = RAt(list, i);
                var sb = new System.Text.StringBuilder();
                foreach (var f in new[] { "bngi", "bnut", "bnuy", "bnuz", "bnva", "bnvb", "bnve" })
                    sb.Append(f).Append('=').Append(RInt(c, f)).Append(' ');
                foreach (var f in new[] { "bnuu", "bnuv", "bnuw", "bnux", "bnvc", "bnvd", "bnvf", "bnvg", "bnvi", "bnvj", "bnvk", "bnvl", "bnvm" })
                {
                    var l2 = RProp(c, f);
                    int n2 = RCount(l2);
                    if (n2 == 0) continue;
                    sb.Append(f).Append("=[");
                    for (int j = 0; j < n2 && j < 12; j++) { try { sb.Append(Convert.ToInt32(RAt(l2, j))).Append(','); } catch { } }
                    sb.Append("] ");
                }
                RawLog("PROBE2 FAVORCFG[" + i + "] " + sb);
            }
        }
        catch (Exception e) { RawLog("PROBE2 FAVORCFG err " + e.Message); }

        try
        {
            var sd = gse.AllSeedSaveData;
            if (sd != null)
            {
                RawLog($"PROBE2 SEED count seed={RCount(sd.SeedSaveDataList)} hybrid={RCount(sd.HybridSeedSaveDataList)} staff={RCount(sd.StaffSeedSaveDataList)}");
                for (int i = 0; i < RCount(sd.SeedSaveDataList) && i < 8; i++)
                {
                    var x = RAt(sd.SeedSaveDataList, i);
                    RawLog($"PROBE2 SEEDDATA[{i}] logic={RInt(x, "SeedLogicID")} cfg={RInt(x, "SeedConfigID")} itemLogic={RInt(x, "ItemLogicID")}");
                }
                for (int i = 0; i < RCount(sd.HybridSeedSaveDataList) && i < 5; i++)
                {
                    var x = RAt(sd.HybridSeedSaveDataList, i);
                    var d = RProp(x, "Data");
                    RawLog($"PROBE2 HYBRID[{i}] seedLogic={RInt(d, "SeedLogicID")} cfg={RInt(d, "SeedConfigID")} itemLogic={RInt(d, "ItemLogicID")} origItem={RInt(x, "OriginalSeedItemID")}");
                }
            }
        }
        catch (Exception e) { RawLog("PROBE2 SEED err " + e.Message); }

        try
        {
            var items = gse.BagSaveData == null ? null : gse.BagSaveData.ItemDataList;
            if (items != null)
                for (int i = 0; i < items.Count; i++)
                {
                    var e = items[i];
                    if (e == null) continue;
                    var cat = PiperNames.CategoryName(PiperNames.ItemCategory(e.ConfigId));
                    bool seedCat = cat.Contains("种子");
                    if (!seedCat && e.SeedLogicID == 0 && e.OriginalSeedItemID == 0) continue;
                    RawLog($"PROBE2 ITEM[{i}] cfg={e.ConfigId} name='{PiperNames.ItemName(e.ConfigId)}' cat={cat} count={e.Count} logic={e.ItemLogicID} seedLogic={e.SeedLogicID} origSeed={e.OriginalSeedItemID} ts={e.Timestamp}");
                }
        }
        catch (Exception e) { RawLog("PROBE2 ITEM err " + e.Message); }
    }

    // h. 玩家反馈自测（v4.17.7，配置门控）：加钱/加好感/加种子后回读校验
    private static void RunFeedbackSelfTest(SaveLoadSystem.GameSaveData gse)
    {
        RawLog("===== SELFTEST 开始 =====");

        // 1) 金币：+1000 金币（raw +1,000,000）后回读并还原
        try
        {
            var ps = gse.AllAttributeSaveData == null ? null : gse.AllAttributeSaveData.AttributeParams;
            long orig = -1;
            if (ps != null) for (int i = 0; i < ps.Count; i++) { var a = ps[i]; if (a != null && a.AttributeId == 901) { orig = a.Value; break; } }
            if (orig >= 0)
            {
                SetAttribute(901, orig + 1000L * 1000);
                long after = -1;
                for (int i = 0; i < ps.Count; i++) { var a = ps[i]; if (a != null && a.AttributeId == 901) { after = a.Value; break; } }
                RawLog($"SELFTEST MONEY orig={orig} after={after} delta={after - orig} expect=1000000");
                SetAttribute(901, orig);
            }
            else RawLog("SELFTEST MONEY 未找到 901");
        }
        catch (Exception e) { RawLog("SELFTEST MONEY err " + e.Message); }

        // 2) 好感：第一个 NPC +100 后回读并还原
        try
        {
            var npcs = gse.AllFavorData == null ? null : gse.AllFavorData.allNPCData;
            if (npcs != null && npcs.Count > 0 && npcs[0] != null)
            {
                int npc = npcs[0].npcID, orig = npcs[0].favorValue;
                SetFavorNpc(npc, orig + 100);
                int after = orig;
                for (int i = 0; i < npcs.Count; i++) { var n = npcs[i]; if (n != null && n.npcID == npc) { after = n.favorValue; break; } }
                RawLog($"SELFTEST FAVOR npc={npc} orig={orig} after={after} delta={after - orig} expect=100");
                SetFavorNpc(npc, orig);
            }
        }
        catch (Exception e) { RawLog("SELFTEST FAVOR err " + e.Message); }

        // 3) 种子：加 3 个土豆种子（10003），检查背包与 SeedSaveData 关联
        try
        {
            int beforeBag = 0, beforeSeed = 0;
            var items = gse.BagSaveData == null ? null : gse.BagSaveData.ItemDataList;
            if (items != null) for (int i = 0; i < items.Count; i++) { var e = items[i]; if (e != null && e.ConfigId == 10003) beforeBag += e.Count; }
            var sd = gse.AllSeedSaveData;
            if (sd != null && sd.SeedSaveDataList != null)
                for (int i = 0; i < sd.SeedSaveDataList.Count; i++) { var x = sd.SeedSaveDataList[i]; if (x != null && x.SeedConfigID == 10003) beforeSeed++; }

            string r = AddNewItem(10003, 3);
            int afterBag = 0, afterSeed = 0, linked = 0;
            if (items != null) for (int i = 0; i < items.Count; i++)
                {
                    var e = items[i]; if (e == null || e.ConfigId != 10003) continue;
                    afterBag += e.Count;
                    if (e.SeedLogicID > 0)
                        for (int j = 0; sd != null && sd.SeedSaveDataList != null && j < sd.SeedSaveDataList.Count; j++)
                        { var x = sd.SeedSaveDataList[j]; if (x != null && x.SeedLogicID == e.SeedLogicID) { linked++; break; } }
                }
            if (sd != null && sd.SeedSaveDataList != null)
                for (int i = 0; i < sd.SeedSaveDataList.Count; i++) { var x = sd.SeedSaveDataList[i]; if (x != null && x.SeedConfigID == 10003) afterSeed++; }
            RawLog($"SELFTEST SEED result='{r}' bagCount {beforeBag}->{afterBag} seedData {beforeSeed}->{afterSeed} linkedItems={linked}");
        }
        catch (Exception e) { RawLog("SELFTEST SEED err " + e.Message); }

        RawLog("===== SELFTEST 结束 =====");
    }

    private static void DumpIntList(string tag, Il2CppSystem.Collections.Generic.List<int> l)
    {
        try
        {
            if (l == null) { RawLog("PROBE2 " + tag + " 为 null"); return; }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < l.Count; i++)
            {
                if (sb.Length > 2000) { sb.Append("…"); break; }
                sb.Append(l[i]).Append(",");
            }
            RawLog("PROBE2 " + tag + "(" + l.Count + "): " + sb);
        }
        catch (Exception e) { RawLog("PROBE2 " + tag + " 异常: " + e.Message); }
    }

    // e+f. 运行时可达性：buo/bjg/ev/bsi/rl 是否能 FindObjectsOfType；ev 找到则 dump 图鉴配置表
    private static void ProbeRuntimeReachable()
    {
        // v4.13.1：buo/bjg/bsi 类在 Steam 构建整类改名，typeof 直连即地雷，全部移除
        // v4.17.5：rl 总管理类也随版本改名，不再参与探测（实例由 SaveAllStaffData 钩子捕获）
        Type[] probeTypes = { typeof(global::ev) };
        string[] names = { "ev(图鉴配置)" };
        for (int i = 0; i < probeTypes.Length; i++)
        {
            try
            {
                var it = Il2CppSystem.Type.GetType(probeTypes[i].FullName + ", Assembly-CSharp");
                if (it == null) { RawLog("PROBE2 REACH " + names[i] + " il2cpp Type 解析失败"); continue; }
                var arr = UnityEngine.Object.FindObjectsOfType(it);
                RawLog("PROBE2 REACH " + names[i] + " 找到 " + (arr == null ? -1 : arr.Length) + " 个"
                    + (arr != null && arr.Length > 0 ? "（首个: " + arr[0].GetIl2CppType().Name + "）" : ""));
            }
            catch (Exception e) { RawLog("PROBE2 REACH " + names[i] + " 异常: " + e.Message); }
        }
        // v4.12.1：ManagerSweep 全类型扫描已禁用（实测崩游戏）；图鉴配置表 ev 实例改为待定向分析
    }

    private static void DumpIllustrationConfigs(Il2CppSystem.Collections.Generic.List<Example.IllustrationConfig> list, int max)
    {
        // v4.17.5：游戏更新后字段名变化（旧 bnlg/bnlh/bnll/bnli/bnlj → 新 bnzm/bnzn/bnzr/bnzo/bnzp），
        // 这里改走反射，兼容两套命名；探测失败不影响主功能。
        for (int i = 0; i < list.Count && i < max; i++)
        {
            var c = list[i];
            if (c == null) continue;
            var sb = new System.Text.StringBuilder();
            int a = RInt(c, "bnlg"); if (a == 0) a = RInt(c, "bnzm");
            int b = RInt(c, "bnlh"); if (b == 0) b = RInt(c, "bnzn");
            int d = RInt(c, "bnll"); if (d == 0) d = RInt(c, "bnzr");
            object so = RProp(c, "bnli") ?? RProp(c, "bnzo");
            object lst = RProp(c, "bnlj") ?? RProp(c, "bnzp");
            int cnt = -1; try { if (lst != null) cnt = Convert.ToInt32(RProp(lst, "Count")); } catch { }
            sb.Append($"a={a} b={b} d={d} str=[{(so as string ?? "")}] condList=({cnt})");
            foreach (var kv in new[] { ("a", a), ("b", b), ("d", d) })
            {
                if (kv.Item2 <= 0) continue;
                var s = PiperNames.ResolveText(kv.Item2);
                if (!string.IsNullOrEmpty(s)) sb.Append($" eua({kv.Item1}={kv.Item2})='{s}'");
            }
            RawLog("PROBE2 ICFG " + sb);
        }
    }

    // ---------- 导出诊断 ----------
    public static string ExportAll()
    {
        try
        {
            var gse = GSE();
            if (gse == null) return "⚠ 未就绪";
            RawLog("===== 导出 =====");
            var noteA = "";
            foreach (var a in GetAttributes(out noteA)) RawLog($"attr {a.AttributeId} = {a.Value}");
            var noteB = "";
            var items = GetItems(out noteB);
            RawLog($"bag note={noteB}; {items.Count} 项");
            foreach (var it in items) RawLog($"  item cfg={it.ConfigId} logic={it.LogicId} count={it.Count} name={PiperNames.ItemName(it.ConfigId)}");
            var noteF = "";
            var npcs = GetFavorNpcs(out noteF);
            RawLog($"favor note={noteF}; {npcs.Count} 人");
            foreach (var n in npcs) RawLog($"  npc {n.NpcId} favor={n.FavorValue} name={PiperNames.NpcName(n.NpcId)}");
            var noteS = "";
            var staff = GetStaff(out noteS);
            RawLog($"staff note={noteS}; {staff.Count} 人");
            foreach (var s2 in staff) RawLog($"  staff {s2.Name} san={s2.CurrentSan}/{s2.MaxSan}");
            var noteF2 = "";
            var fs = GetFactions(out noteF2);
            foreach (var f in fs) RawLog($"  faction {f.FactionId} ({PiperBridge.FactionName(f.FactionId)}) lv={f.Level} exp={f.Exp}");
            RawLog("===== 导出结束 =====");
            return "✔ 已导出到 LogOutput.log";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // ---------- 好感 ----------
    public class FavorNpcRow { public int NpcId; public int FavorValue; }

    public static List<FavorNpcRow> GetFavorNpcs(out string note)
    {
        var list = new List<FavorNpcRow>();
        note = "";
        var gse = GSE();
        if (gse == null) { note = "GameSaveEntity 未就绪"; return list; }
        try
        {
            var npcs = gse.AllFavorData.allNPCData;
            if (npcs == null) { note = "allNPCData 为空"; return list; }
            for (int i = 0; i < npcs.Count; i++)
            {
                var n = npcs[i];
                if (n == null) continue;
                list.Add(new FavorNpcRow { NpcId = n.npcID, FavorValue = n.favorValue });
            }
        }
        catch (Exception e) { note = "读取异常: " + e.Message; }
        return list;
    }

    public static string SetFavorNpc(int npcId, int target)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        try
        {
            var npcs = gse.AllFavorData.allNPCData;
            int hit = 0;
            for (int i = 0; i < npcs.Count; i++)
            {
                var n = npcs[i];
                if (n != null && n.npcID == npcId) { n.favorValue = Math.Max(0, target); hit++; }
            }
            if (hit == 0) return "⚠ 未找到 npcID=" + npcId;
            try { gse.AllFavorData.Load(); } catch (Exception e) { return "已改写但 Load() 异常: " + e.Message; }
            RawLog($"SetFavorNpc {npcId} -> {target} x{hit}");
            Toast($"NPC {npcId} 好感 → {target}");
            return $"✔ NPC {npcId} 好感 → {target}";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // ---------- 势力 ----------
    public class FactionRow { public int FactionId; public int Level; public int Exp; }

    public static List<FactionRow> GetFactions(out string note)
    {
        var list = new List<FactionRow>();
        note = "";
        var gse = GSE();
        if (gse == null) { note = "GameSaveEntity 未就绪"; return list; }
        try
        {
            var fs = gse.AllFavorData.allFavorFactionData;
            if (fs == null) { note = "allFavorFactionData 为空"; return list; }
            for (int i = 0; i < fs.Count; i++)
            {
                var f = fs[i];
                if (f == null) continue;
                list.Add(new FactionRow { FactionId = f.favorFactionID, Level = f.currentLevel, Exp = f.currentExp });
            }
        }
        catch (Exception e) { note = "读取异常: " + e.Message; }
        return list;
    }

    public static string AddFactionExp(int factionId, int delta)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        try
        {
            var fs = gse.AllFavorData.allFavorFactionData;
            int hit = 0;
            for (int i = 0; i < fs.Count; i++)
            {
                var f = fs[i];
                if (f != null && f.favorFactionID == factionId) { f.currentExp = Math.Max(0, f.currentExp + delta); hit++; }
            }
            if (hit == 0) return "⚠ 未找到 势力ID=" + factionId;
            try { gse.AllFavorData.Load(); } catch (Exception e) { return "已改写但 Load() 异常: " + e.Message; }
            RawLog($"AddFactionExp {factionId} {delta:+0;-0}");
            Toast($"势力 {factionId} 经验 {(delta >= 0 ? "+" : "")}{delta}");
            return $"✔ 势力 {factionId} 经验 {delta:+0;-0}";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // 阵营装饰/信物领取标志（解锁[装饰]类物品走这里，与背包无关）
    public static string ClaimFactionRewards(int factionId, bool decoration, bool keepsake)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        try
        {
            var fs = gse.AllFavorData.allFavorFactionData;
            int hit = 0;
            for (int i = 0; i < fs.Count; i++)
            {
                var f = fs[i];
                if (f != null && f.favorFactionID == factionId)
                {
                    if (decoration) f.decorationClaimed = true;
                    if (keepsake) f.keepsakeClaimed = true;
                    hit++;
                }
            }
            if (hit == 0) return "⚠ 未找到 势力ID=" + factionId;
            try { gse.AllFavorData.Load(); } catch (Exception e) { return "已改写但 Load() 异常: " + e.Message; }
            RawLog($"ClaimFactionRewards {factionId} deco={decoration} keep={keepsake}");
            Toast($"势力 {factionId} 奖励已标记领取");
            return $"✔ 势力 {factionId} 奖励已标记领取";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    public static string AddFactionLevel(int factionId, int delta)
    {
        var gse = GSE();
        if (gse == null) return "⚠ 未就绪";
        try
        {
            var fs = gse.AllFavorData.allFavorFactionData;
            int hit = 0;
            for (int i = 0; i < fs.Count; i++)
            {
                var f = fs[i];
                if (f != null && f.favorFactionID == factionId) { f.currentLevel = Math.Max(0, f.currentLevel + delta); hit++; }
            }
            if (hit == 0) return "⚠ 未找到 势力ID=" + factionId;
            try { gse.AllFavorData.Load(); } catch (Exception e) { return "已改写但 Load() 异常: " + e.Message; }
            RawLog($"AddFactionLevel {factionId} {delta:+0;-0}");
            Toast($"势力 {factionId} 等级 {(delta >= 0 ? "+" : "")}{delta}");
            return $"✔ 势力 {factionId} 等级 {delta:+0;-0}";
        }
        catch (Exception e) { return "✘ " + e.Message; }
    }

    // ---------- 员工（v4.16：实时优先，存档树兜底显示） ----------
    public class StaffRow { public int Idx; public string Name; public int CurrentSan; public int MaxSan; public bool Live; }

    public static List<StaffRow> GetStaff(out string note)
    {
        var list = new List<StaffRow>();
        note = "";
        // 实时模式：直接读运行时实体（游戏内真实值，改完立即可见）
        string liveNote = null;
        try
        {
            var live = StaffLive.ReadRows(out liveNote);
            if (live.Count > 0)
            {
                for (int i = 0; i < live.Count; i++)
                {
                    var r = live[i];
                    list.Add(new StaffRow { Idx = r.SdIdx, Name = r.Name, CurrentSan = r.Cur, MaxSan = r.Max, Live = true });
                }
                note = "实时";
                return list;
            }
        }
        catch (Exception e) { liveNote = "实时读取异常: " + e.Message; }
        // 兜底：存档树（上次存档时的值，仅供查看；回满按钮已被实时引擎接管）
        var gse = GSE();
        if (gse == null) { note = liveNote ?? "GameSaveEntity 未就绪"; return list; }
        try
        {
            var staff = gse.AllStaffSaveData.StaffList;
            if (staff == null) { note = liveNote ?? "StaffList 为空"; return list; }
            for (int i = 0; i < staff.Count; i++)
            {
                var s = staff[i];
                if (s == null) continue;
                list.Add(new StaffRow { Idx = i, Name = s.Name ?? ("#" + i), CurrentSan = s.CurrentSan, MaxSan = s.MaxSan, Live = false });
            }
            note = liveNote ?? "存档值（实时未连接）";
        }
        catch (Exception e) { note = "读取异常: " + e.Message; }
        return list;
    }

    public static string MaxStaffSan(int index)
    {
        // v4.16：唯一正解是运行时实体直改——存档树写入会被 autosave 的 SaveAllStaffData
        // 全量覆盖（永远到不了磁盘），官方 Load() 又会重建员工系统（v4.9 罢工事故）。
        return StaffLive.SetRowFull(index);
    }

    // 一键回满全部员工理智（跳过 MaxSan<=0 的特殊角色）
    public static string MaxAllStaffSan()
    {
        return StaffLive.SetAllFull();
    }
}
