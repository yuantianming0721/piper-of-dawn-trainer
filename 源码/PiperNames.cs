using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;

namespace PiperTrainer;

// v4.0 文本解析子系统：
// 文本表 = go.fyu() → .fyw() → mb.bnnt : List<Example.LanguageTalk>（每条含 id + 4 语言列）
// 名字解析 = 配置(NameID int) → 文本表。
// NameID 字段自动识别：候选 int 字段逐一与文本表比对命中率。
public static class PiperNames
{
    public static bool Ready;
    public static string Status = "未初始化";
    public static int TextCount;

    public static readonly Dictionary<int, string> Texts = new Dictionary<int, string>();     // LanguageTalk id -> 中文
    public static readonly Dictionary<int, string> NpcNames = new Dictionary<int, string>();  // npcID -> 名字
    public static readonly Dictionary<int, string> ItemNames = new Dictionary<int, string>(); // 物品cfgID -> 名字
    public static readonly Dictionary<int, int> ItemCategories = new Dictionary<int, int>();  // 物品cfgID -> 类别(lx枚举)
    public static readonly Dictionary<int, string> CategoryLabels = new Dictionary<int, string>();
    private static readonly Dictionary<string, string> _catCn = new Dictionary<string, string>
    {
        { "E_CropSeed", "种子" }, { "E_AlchemySeed", "炼金种子" }, { "E_HybridSeed", "杂交种子" },
        { "E_Crop", "作物" }, { "E_Goods", "商品" }, { "E_Material", "材料" },
        { "E_Product", "产物" }, { "E_Treasures", "珍宝" }, { "E_Fertilizer", "肥料" },
        { "E_Relic", "遗物" }, { "E_Recipe", "配方" }, { "E_Poker", "筹码" },
        { "E_FishingTool", "渔具" }
    };

    public static int ItemCategory(int id) => ItemCategories.TryGetValue(id, out var c) ? c : -1;
    public static string CategoryName(int cat) => CategoryLabels.TryGetValue(cat, out var n) ? n : ("类" + cat);

    private static void BuildCategoryLabels()
    {
        if (CategoryLabels.Count > 0) return;
        try
        {
            var t = typeof(Example.lx);
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public))
            {
                if (f.Name == "value__") continue;
                try
                {
                    int v = Convert.ToInt32(f.GetValue(null));
                    string cn;
                    if (!_catCn.TryGetValue(f.Name, out cn)) cn = f.Name;
                    CategoryLabels[v] = cn;
                }
                catch { }
            }
        }
        catch { }
    }

    private static int _attempts;

    public static string NpcName(int id) => NpcNames.TryGetValue(id, out var n) ? n : "NPC" + id;
    public static string ItemName(int id) => ItemNames.TryGetValue(id, out var n) ? n : "物品" + id;

    private static string CacheDir => Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx", "config");
    private static string NpcCachePath => Path.Combine(CacheDir, "PiperTrainerNpcNames.txt");
    private static string ItemCachePath => Path.Combine(CacheDir, "PiperTrainerItemNames.txt");

    // ---------- 反射调用辅助（静态/实例都试） ----------

    private static object CallStatic(Type t, string name)
    {
        try
        {
            var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (m != null) return m.Invoke(null, null);
        }
        catch { }
        return null;
    }

    private static object CallOn(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var m = obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null) return null;
            return m.IsStatic ? m.Invoke(null, null) : m.Invoke(obj, null);
        }
        catch { return null; }
    }

    private static object Prop(object obj, string name)
    {
        if (obj == null) return null;
        try
        {
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p?.GetValue(obj);
        }
        catch { return null; }
    }

    private static object CallOnArg(object obj, string name, object arg)
    {
        if (obj == null) return null;
        try
        {
            var m = obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, new[] { arg.GetType() }, null)
                 ?? obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, new[] { typeof(int) }, null);
            if (m == null) return null;
            return m.IsStatic ? m.Invoke(null, new[] { arg }) : m.Invoke(obj, new[] { arg });
        }
        catch { return null; }
    }

    private static MethodInfo[] _textMis;
    private static int _textBest = -1;

    // 文本解析器：旧版 bs.eua(Int32)；2026-10-21 新版改为 bs.evb / bs.evc(Int32)。
    // 三个都试，取第一个返回非空的方法并缓存。
    private static MethodInfo[] TextMethods()
    {
        if (_textMis != null) return _textMis;
        try
        {
            var bsT = typeof(Example.UnitConfig).Assembly.GetType("bs");
            if (bsT == null) return _textMis = new MethodInfo[0];
            var list = new System.Collections.Generic.List<MethodInfo>();
            foreach (var name in new[] { "eua", "evb", "evc" })
            {
                var m = bsT.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
                if (m != null) list.Add(m);
            }
            return _textMis = list.ToArray();
        }
        catch { return _textMis = new MethodInfo[0]; }
    }

    public static string ResolveText(int id)
    {
        try
        {
            var ms = TextMethods();
            if (ms.Length == 0) return null;
            if (_textBest >= 0 && _textBest < ms.Length)
            {
                var s0 = ms[_textBest].Invoke(null, new object[] { id }) as string;
                if (!string.IsNullOrEmpty(s0)) return s0;
            }
            for (int i = 0; i < ms.Length; i++)
            {
                var s = ms[i].Invoke(null, new object[] { id }) as string;
                if (!string.IsNullOrEmpty(s)) { _textBest = i; return s; }
            }
            return null;
        }
        catch { return null; }
    }

    private static object CallStaticArg(Type t, string name, int arg)
    {
        try
        {
            var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null)
                 ?? t.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(long) }, null);
            if (m == null) return null;
            var p = m.GetParameters()[0];
            object v = p.ParameterType == typeof(long) ? (object)(long)arg : arg;
            return m.Invoke(null, new[] { v });
        }
        catch (Exception e) { return "ERR:" + e.Message; }
    }

    private static int CjkRatio(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int cjk = 0;
        foreach (var c in s) if (c >= 0x4e00 && c <= 0x9fff) cjk++;
        return cjk * 100 / s.Length;
    }

    // ---------- 初始化 ----------

    public static void TryInit(bool force = false)
    {
        if (Ready && !force) return;
        if (_attempts > 60 && !force) return;
        _attempts++;
        try
        {
            if (BuildTextTable())
            {
                BuildNpcNames();
                BuildItemNames();
                Ready = true;
                Status = $"就绪：文本 {TextCount} 条 / NPC名 {NpcNames.Count} / 物品名 {ItemNames.Count}";
                PiperTrainerPlugin.Log2?.LogInfo("NAMES " + Status);
            }
            else
            {
                Status = "文本表未就绪（等游戏场景加载后自动重试）";
            }
        }
        catch (Exception e)
        {
            Status = "初始化异常: " + e.Message;
            PiperTrainerPlugin.Log2?.LogInfo("NAMES err " + e);
        }
    }

    private static bool BuildTextTable()
    {
        if (Texts.Count > 0) return true;
        var goInst = CallStatic(typeof(go), "fyu");
        if (goInst == null) return false;
        var mb = CallOn(goInst, "fyw");
        var list = Prop(mb, "bnnt") as Il2CppSystem.Collections.Generic.List<Example.LanguageTalk>;
        if (list == null || list.Count == 0) return false;

        // 探测 4 列中哪列是中文：抽样统计 CJK 占比
        int n0 = 0, n1 = 0, n2 = 0, n3 = 0, samples = 0;
        for (int i = 0; i < list.Count && samples < 300; i += Math.Max(1, list.Count / 300))
        {
            var e = list[i];
            if (e == null) continue;
            n0 += CjkRatio(Prop(e, "bnnn") as string);
            n1 += CjkRatio(Prop(e, "bnnp") as string);
            n2 += CjkRatio(Prop(e, "bnnq") as string);
            n3 += CjkRatio(Prop(e, "bnnr") as string);
            samples++;
        }
        string col = "bnnn";
        int best = n0;
        if (n1 > best) { best = n1; col = "bnnp"; }
        if (n2 > best) { best = n2; col = "bnnq"; }
        if (n3 > best) { best = n3; col = "bnnr"; }
        PiperTrainerPlugin.Log2?.LogInfo($"NAMES 中文列={col} (比例{best}/{Math.Max(1, samples)})");

        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null) continue;
            var idObj = Prop(e, "bnnm");
            if (idObj == null) continue;
            int id = Convert.ToInt32(idObj);
            var txt = Prop(e, col) as string;
            if (!string.IsNullOrEmpty(txt)) Texts[id] = txt;
        }
        TextCount = Texts.Count;
        return TextCount > 0;
    }

    private static readonly Dictionary<int, Example.UnitConfig> _unitById = new Dictionary<int, Example.UnitConfig>();

    private static void LoadUnits()
    {
        if (_unitById.Count > 0) return;
        var hvInst = CallStatic(typeof(hv), "gcp");
        var ol = CallOn(hvInst, "gcr");
        var ucList = Prop(ol, "bocn") as Il2CppSystem.Collections.Generic.List<Example.UnitConfig>;
        if (ucList == null) return;
        for (int k = 0; k < ucList.Count; k++)
        {
            var uc = ucList[k];
            if (uc == null) continue;
            try
            {
                var idv = Convert.ToInt32(Prop(uc, "bobt") ?? -1);
                if (idv > 0) _unitById[idv] = uc;
            }
            catch { }
        }
        PiperTrainerPlugin.Log2?.LogInfo("NAMES UnitConfig 载入 " + _unitById.Count);
    }

    private static string _unitNameField;

    private static void BuildNpcNames()
    {
        LoadUnits();
        if (_unitById.Count == 0) return;

        var fwInst = CallStatic(typeof(fw), "fws");
        var kv = CallOn(fwInst, "fwu");
        var list = Prop(kv, "bnhe") as Il2CppSystem.Collections.Generic.List<Example.FavorNPCConfig>;
        if (list == null) return;

        int built = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null) continue;
            try
            {
                int npcId = Convert.ToInt32(Prop(c, "bngi") ?? -1);
                if (npcId <= 0) continue;
                if (_unitById.TryGetValue(npcId, out var uc) && uc != null)
                {
                    int nameId = Convert.ToInt32(Prop(uc, "bobu") ?? -1);
                    var nm = nameId > 0 ? ResolveText(nameId) : null;
                    if (!string.IsNullOrEmpty(nm))
                        NpcNames[npcId] = nm;
                }
            }
            catch { }
        }
        built = NpcNames.Count;
        PiperTrainerPlugin.Log2?.LogInfo("NAMES NPC名(eua) 解析 " + built + " 条");
        int shown = 0;
        foreach (var kvp in NpcNames)
        {
            PiperTrainerPlugin.Log2?.LogInfo("NAMESDBG npcname " + kvp.Key + "=" + kvp.Value);
            if (++shown >= 12) break;
        }
        if (built > 0) SaveCache(NpcCachePath, NpcNames);
    }

    private static void BuildItemNames()
    {
        BuildCategoryLabels();
        var glInst = CallStatic(typeof(gl), "fyl");
        var lt = CallOn(glInst, "fyn");
        var list = Prop(lt, "bnmv") as Il2CppSystem.Collections.Generic.List<Example.ItemConfig>;
        if (list == null) return;

        string[] intFields = { "bnmb", "bnmc", "bnmd", "bnmf", "bnmh", "bnmi", "bnmj", "bnmk", "bnml", "bnmm", "bnmr", "bnms", "bnmt" };

        // 用 eua 命中率识别 NameID 字段（返回短中文 = 名字）
        string nameField = null; int bestScore = 0;
        foreach (var f in intFields)
        {
            int hit = 0;
            int n0 = Math.Min(list.Count, 40);
            string samp = null;
            for (int i = 0; i < n0; i++)
            {
                var c = list[i];
                if (c == null) continue;
                try
                {
                    int v = Convert.ToInt32(Prop(c, f) ?? -1);
                    if (v <= 0) continue;
                    var s = ResolveText(v);
                    if (!string.IsNullOrEmpty(s) && s.Length <= 14 && CjkRatio(s) > 50)
                    { hit++; if (samp == null) samp = s; }
                }
                catch { }
            }
            PiperTrainerPlugin.Log2?.LogInfo("NAMESDBG itemeua." + f + ": " + hit + "/" + n0 + " 例='" + samp + "'");
            if (hit > bestScore) { bestScore = hit; nameField = f; }
        }
        PiperTrainerPlugin.Log2?.LogInfo("NAMES 物品NameID字段=" + (nameField ?? "?") + " 分=" + bestScore);
        if (nameField == null || bestScore < 5) return;

        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null) continue;
            try
            {
                int itemId = Convert.ToInt32(Prop(c, "bnmb") ?? -1);
                int nameId = Convert.ToInt32(Prop(c, nameField) ?? -1);
                if (itemId <= 0 || nameId <= 0) continue;
                var nm = ResolveText(nameId);
                if (!string.IsNullOrEmpty(nm)) ItemNames[itemId] = nm;
                try { ItemCategories[itemId] = Convert.ToInt32(Prop(c, "bnme") ?? -1); } catch { }
            }
            catch { }
        }
        PiperTrainerPlugin.Log2?.LogInfo("NAMES 物品名(eua) 解析 " + ItemNames.Count + " 条");
        if (ItemNames.Count > 0) SaveCache(ItemCachePath, ItemNames);
    }

    private static string DetectField(Il2CppSystem.Collections.Generic.List<Example.ItemConfig> list, string[] fields, HashSet<int> known, Dictionary<int, string> textTable)
    {
        string best = null; int bestScore = 0;
        int sample = Math.Min(list.Count, 40);
        foreach (var f in fields)
        {
            int hit = 0, total = 0;
            string sample1 = null; int sample1v = 0;
            for (int i = 0; i < sample; i++)
            {
                var c = list[i];
                if (c == null) continue;
                var v = Prop(c, f);
                if (v == null) continue;
                int val = Convert.ToInt32(v);
                total++;
                if (textTable != null)
                {
                    if (textTable.TryGetValue(val, out var t) && CjkRatio(t) > 30 && t.Length <= 16)
                    { hit++; if (sample1 == null) { sample1 = t; sample1v = val; } }
                }
                else
                {
                    if (val >= 10000 && val <= 99999) hit++;
                }
            }
            if (total > 0)
                PiperTrainerPlugin.Log2?.LogInfo($"NAMESDBG item.{f}: {hit}/{total} 例: {sample1v}='{sample1}'");
            if (total > 0 && hit > bestScore) { bestScore = hit; best = f; }
        }
        return bestScore > 1 ? best : null;
    }

    private static bool _npcDirty = true;

    private static void SaveCache(string path, Dictionary<int, string> map)
    {
        try
        {
            var lines = new List<string>();
            foreach (var kv in map)
            {
                int cat = -1;
                ItemCategories.TryGetValue(kv.Key, out cat);
                lines.Add(cat >= 0 ? kv.Key + "|" + kv.Value + "|" + cat : kv.Key + "|" + kv.Value);
            }
            File.WriteAllLines(path, lines.ToArray(), System.Text.Encoding.UTF8);
        }
        catch { }
    }

    private static void LoadCache(string path, Dictionary<int, string> map)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path, System.Text.Encoding.UTF8))
            {
                var i = line.IndexOf('|');
                if (i <= 0) continue;
                if (!int.TryParse(line.Substring(0, i), out var id)) continue;
                var rest = line.Substring(i + 1);
                var j = rest.LastIndexOf('|');
                if (j > 0 && int.TryParse(rest.Substring(j + 1), out var cat))
                {
                    map[id] = rest.Substring(0, j);
                    if (path.Contains("Item")) ItemCategories[id] = cat;
                }
                else map[id] = rest;
            }
        }
        catch { }
    }

    // 启动即尝试加载缓存（无需游戏内也可用旧表）
    public static void LoadCaches()
    {
        BuildCategoryLabels();
        LoadCache(NpcCachePath, NpcNames);
        LoadCache(ItemCachePath, ItemNames);
        if (NpcNames.Count > 0 || ItemNames.Count > 0)
            Status = $"缓存已载入：NPC名 {NpcNames.Count} / 物品名 {ItemNames.Count}";
    }
}
