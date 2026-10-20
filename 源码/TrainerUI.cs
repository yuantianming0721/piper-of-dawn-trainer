using System;
using System.Collections.Generic;
using UnityEngine;

namespace PiperTrainer;

// v2.4：统一"步长"体系——顶栏选择步长（选中高亮），金钱/背包/好感全部按步长加减，可加可减
public static class TrainerUI
{
    private static Rect _rect = new Rect(60, 60, 560, 700);
    private static string _status = "";
    private static double _statusTime;
    private static int _tab;          // 0=金钱 1=背包 2=好感 3=员工
    private static int _page;
    private static bool _drag;
    private static Vector2 _dragOff;
    private static int _errCount;

    private static double _lastMoneyRefresh;
    private static long _moneyValue;
    private static bool _moneyKnown;

    private static long _step = 10000;   // 当前步长
    private static readonly long[] _steps = { 1, 100, 1000, 10000, 100000, 1000000, 10000000 };
    private static readonly string[] _stepNames = { "1", "100", "1千", "1万", "10万", "100万", "1000万" };

    private static List<PiperBridge.ItemRow> _items;
    private static string _itemsNote = "";
    private static int _catFilter = -1;
    private static string _search = "";
    private static bool _showAll;
    private static List<PiperBridge.ItemRow> _shown;
    private static string _shownKey = "";
    private static List<PiperBridge.FavorNpcRow> _npcs;
    private static string _npcsNote = "";
    private static List<PiperBridge.StaffRow> _staff;
    private static string _staffNote = "";
    private static List<PiperBridge.BuildingRow> _blds;
    private static string _bldsNote = "";
    private static int _bFilter;   // 0=全部 1=未拥有 2=已拥有

    private const int Rows = 7;
    private const float RowH = 34f;

    public static Rect PanelRect => _rect;
    public static int CurrentTab => _tab;
    // 开发调试：程序化切换页签（DebugCapture 截图巡检用），与点 Tab 按钮同逻辑
    public static void DebugSwitchTab(int i)
    {
        _tab = i; _page = 0;
        if (i == 1) _items = PiperBridge.GetItems(out _itemsNote);
        if (i == 2) _npcs = PiperBridge.GetFavorNpcs(out _npcsNote);
        if (i == 3) _staff = PiperBridge.GetStaff(out _staffNote);
        if (i == 4) _blds = PiperBridge.GetBuildings(out _bldsNote);
    }
    // 背包页搜索行在面板内的固定位置（与 DrawInner 布局一致）
    public static Rect SearchFieldRect => new Rect(_rect.x + 56, _rect.y + 118, 220, 24);
    // 打字的搜索框放在面板右侧外部（避开 IMGUI 绘制层，保证可见可点）
    public static Rect ExternalSearchRect => new Rect(_rect.x + _rect.width + 8, _rect.y + 118, 240, 30);

    public static void Draw()
    {
        try { DrawInner(); }
        catch (Exception e)
        {
            if (_errCount++ < 3)
                PiperTrainerPlugin.Log2?.LogError("gui: " + e);
        }
    }

    private static void Flash(string msg)
    {
        _status = msg;
        _statusTime = Time.realtimeSinceStartup;
    }

    private static void RefreshShown()
    {
        string key = (_items == null ? "null" : _items.Count.ToString()) + "|" + _catFilter + "|" + _search + "|" + (_showAll ? "A" : "B");
        if (key == _shownKey && _shown != null) return;
        _shownKey = key;
        _page = 0;
        _shown = new List<PiperBridge.ItemRow>();
        if (_showAll)
        {
            // 全部物品模式：来源=物品名总表；持有数从 _items 汇总
            var owned = new Dictionary<int, int>();
            if (_items != null)
                foreach (var it in _items)
                {
                    owned.TryGetValue(it.ConfigId, out var c);
                    owned[it.ConfigId] = c + it.Count;
                }
            foreach (var kv in PiperNames.ItemNames)
            {
                if (_catFilter >= 0 && PiperNames.ItemCategory(kv.Key) != _catFilter) continue;
                var nm = kv.Value ?? "";
                if (!string.IsNullOrEmpty(_search) && nm.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                owned.TryGetValue(kv.Key, out var oc);
                _shown.Add(new PiperBridge.ItemRow { ConfigId = kv.Key, Count = oc, Idx = -1, LogicId = -1 });
            }
            _shown.Sort((a, bb) => a.ConfigId.CompareTo(bb.ConfigId));
        }
        else
        {
            if (_items == null) return;
            foreach (var it in _items)
            {
                if (_catFilter >= 0 && PiperNames.ItemCategory(it.ConfigId) != _catFilter) continue;
                var nm = PiperNames.ItemName(it.ConfigId) ?? "";
                if (!string.IsNullOrEmpty(_search) && nm.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _shown.Add(it);
            }
        }
    }

    private static void HandleDrag()
    {
        try
        {
            var e = Event.current;
            if (e == null) return;
            int t = (int)e.type; // MouseDown=0 MouseUp=1 MouseDrag=3
            var title = new Rect(_rect.x, _rect.y, _rect.width, 26);
            if (!_drag && t == 0 && title.Contains(e.mousePosition))
            {
                _drag = true;
                _dragOff = e.mousePosition - new Vector2(_rect.x, _rect.y);
                e.Use();
            }
            else if (_drag && t == 3)
            {
                _rect.x = e.mousePosition.x - _dragOff.x;
                _rect.y = e.mousePosition.y - _dragOff.y;
                e.Use();
            }
            else if (_drag && t == 1)
            {
                _drag = false;
                e.Use();
            }
        }
        catch { }
    }

    private static void RefreshMoney()
    {
        var note = "";
        var attrs = PiperBridge.GetAttributes(out note);
        foreach (var a in attrs)
        {
            if (a.AttributeId == 901) { _moneyValue = a.Value; _moneyKnown = true; return; }
        }
        _moneyKnown = false;
        if (!string.IsNullOrEmpty(note) && _errCount < 8) { PiperTrainerPlugin.Log2?.LogInfo("money refresh: " + note); _errCount++; }
    }

    private static string StepName(long step)
    {
        for (int i = 0; i < _steps.Length; i++)
            if (_steps[i] == step) return _stepNames[i];
        return step.ToString();
    }

    private static void DrawInner()
    {
        HandleDrag();

        GUI.Box(new Rect(_rect.x, _rect.y, _rect.width, _rect.height), "");
        GUI.Label(new Rect(_rect.x + 10, _rect.y + 4, _rect.width - 110, 22),
            "吹笛人 · 内置修改器 v" + PiperTrainerPlugin.Ver);

        float x = _rect.x + 12;
        float w = _rect.width - 24;
        float y = _rect.y + 30;

        // ---- Tab ----
        string[] tabs = { "金钱", "背包", "好感", "员工", "建筑" };
        float tw = (w - 16) / 5f;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (GUI.Button(new Rect(x + i * (tw + 4), y, tw, 26), tabs[i] + (_tab == i ? " ●" : "")))
            {
                _tab = i; _page = 0;
                if (i == 1) _items = PiperBridge.GetItems(out _itemsNote);
                if (i == 2) _npcs = PiperBridge.GetFavorNpcs(out _npcsNote);
                if (i == 3) _staff = PiperBridge.GetStaff(out _staffNote);
                if (i == 4) _blds = PiperBridge.GetBuildings(out _bldsNote);
            }
        }
        y += 32;

        // ---- 步长选择（全局，选中高亮） ----
        GUI.Label(new Rect(x, y + 4, 34, 22), "步长");
        float sw = (w - 40) / 7f;
        for (int i = 0; i < _steps.Length; i++)
        {
            bool sel = _step == _steps[i];
            var r = new Rect(x + 38 + i * (sw + 3), y, sw, 24);
            if (sel)
            {
                var oldC = GUI.color;
                GUI.color = new Color(0.30f, 0.52f, 0.28f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = oldC;
                GUI.Label(r, _stepNames[i]);
            }
            else if (GUI.Button(r, _stepNames[i]))
            {
                _step = _steps[i];
            }
        }
        y += 32;

        var gse = PiperBridge.GSE();
        if (gse == null)
        {
            GUI.Label(new Rect(x, y, w, 44), "⚠ GameSaveEntity 未就绪：请读取存档进入游戏后再按 Home。");
            return;
        }

        // ============ 金钱 ============
        if (_tab == 0)
        {
            if (Time.realtimeSinceStartup - _lastMoneyRefresh > 1.0)
            {
                _lastMoneyRefresh = Time.realtimeSinceStartup;
                RefreshMoney();
            }
            GUI.Label(new Rect(x, y, 300, 22), "【金币】" +
                (_moneyKnown ? $"当前 {_moneyValue / 100.0:0.##}" : "未找到 901（导出诊断日志查属性表）"));
            y += 26;
            if (GUI.Button(new Rect(x, y, 150, 30), $"加 {StepName(_step)}"))
                Flash(PiperBridge.SetAttribute(901, _step * 100));
            if (GUI.Button(new Rect(x + 156, y, 150, 30), $"减 {StepName(_step)}"))
                Flash(PiperBridge.SetAttribute(901, Math.Max(0, _moneyValue / 100 - _step) * 100));
            if (GUI.Button(new Rect(x + 312, y, 110, 30), "翻倍"))
                Flash(PiperBridge.SetAttribute(901, Math.Max(0, _moneyValue) * 2));
            if (GUI.Button(new Rect(x + 428, y, 100, 30), "刷新"))
                RefreshMoney();
            y += 40;
            GUI.Label(new Rect(x, y, w, 22), "步长单位=金币；改完立即生效并随存档保存。");
            y += 28;
        }

        // ============ 背包 ============
        if (_tab == 1)
        {
            GUI.Label(new Rect(x, y, 300, 22), _showAll
                ? ("【全部物品】" + PiperNames.ItemNames.Count + " 种（可添加未持有）")
                : ("【背包】" + (_items == null ? "" : _items.Count + " 项")));
            if (GUI.Button(new Rect(x + w - 126, y, 64, 22), "结构探测"))
            {
                PiperBridge.DumpStructures(false);
                Flash("探测已执行（明细见 LogOutput.log）");
            }
            if (GUI.Button(new Rect(x + w - 60, y, 60, 22), "刷新"))
                _items = PiperBridge.GetItems(out _itemsNote);
            y += 24;

            // 模式切换 + 搜索状态
            try
            {
                if (GUI.Button(new Rect(x, y, 86, 24), _showAll ? "切回背包中" : "全部物品»"))
                    _showAll = !_showAll;
                if (UguiInput.Active)
                {
                    try { var fv = UguiInput.Field.text; if (fv != _search) _search = fv; } catch { }
                    GUI.Label(new Rect(x + 92, y + 4, 170, 22), _showAll ? "搜全部 654 种物品" : "打字搜索↓右侧框");
                    if (GUI.Button(new Rect(x + w - 56, y, 56, 24), "清搜索"))
                    { _search = ""; try { UguiInput.Field.text = ""; } catch { } }
                }
                else
                {
                    GUI.Label(new Rect(x + 92, y + 4, 200, 22), "（打字框未就绪，可用分类）");
                }
            }
            catch { }
            y += 28;

            // 分类筛选（按游戏自身物品分类）
            try
            {
                if (GUI.Button(new Rect(x, y, 60, 24), _catFilter < 0 ? "[全部]" : "全部"))
                    _catFilter = -1;
                float bx = x + 64;
                int shownCats = 0;
                var catsSeen = new List<int>();
                if (_showAll)
                {
                    foreach (var kv in PiperNames.ItemNames)
                    {
                        int c = PiperNames.ItemCategory(kv.Key);
                        if (c >= 0 && !catsSeen.Contains(c)) catsSeen.Add(c);
                    }
                }
                else if (_items != null)
                {
                    foreach (var it in _items)
                    {
                        int c = PiperNames.ItemCategory(it.ConfigId);
                        if (c >= 0 && !catsSeen.Contains(c)) catsSeen.Add(c);
                    }
                }
                foreach (var c in catsSeen)
                {
                    if (shownCats >= 7) break;
                    string label = PiperNames.CategoryName(c);
                    bool sel = _catFilter == c;
                    if (GUI.Button(new Rect(bx, y, 56, 24), sel ? "[" + label + "]" : label))
                        _catFilter = sel ? -1 : c;
                    bx += 60;
                    shownCats++;
                }
            }
            catch { }
            y += 28;

            if (_items == null && !_showAll)
            {
                GUI.Label(new Rect(x, y, w, 22), _itemsNote);
                y += RowH;
            }
            else
            {
                RefreshShown();
                var list = _shown;
                int total = list.Count;
                int maxPage = Math.Max(1, (total + Rows - 1) / Rows);
                if (_page >= maxPage) _page = maxPage - 1;
                if (_page < 0) _page = 0;
                GUI.Label(new Rect(x + w - 170, y - 2, 100, 22), $"{_page + 1}/{maxPage}页");
                int end = Math.Min(total, (_page + 1) * Rows);
                for (int i = _page * Rows; i < end; i++)
                {
                    var it = list[i];
                    if (_showAll)
                    {
                        GUI.Label(new Rect(x, y + 4, 240, 22),
                            $"{PiperNames.ItemName(it.ConfigId)}" + (it.Count > 0 ? $"（持有{it.Count}）" : ""));
                        if (GUI.Button(new Rect(x + w - 116, y, 116, 26), $"添加+{StepName(_step)}"))
                        {
                            Flash(PiperBridge.AddNewItem(it.ConfigId, (int)Math.Min(_step, int.MaxValue)));
                            _items = PiperBridge.GetItems(out _itemsNote);
                        }
                    }
                    else
                    {
                        GUI.Label(new Rect(x, y + 4, 200, 22), $"{PiperNames.ItemName(it.ConfigId)}  ×{it.Count}");
                        int lid = it.LogicId;
                        if (GUI.Button(new Rect(x + 206, y, 76, 26), $"+{StepName(_step)}"))
                        {
                            Flash(PiperBridge.SetItemCount(lid, (int)Math.Min(_step, int.MaxValue)));
                            _items = PiperBridge.GetItems(out _itemsNote);
                        }
                        if (GUI.Button(new Rect(x + 286, y, 76, 26), $"−{StepName(_step)}"))
                        {
                            Flash(PiperBridge.SetItemCount(lid, Math.Max(0, it.Count - (int)Math.Min(_step, int.MaxValue))));
                            _items = PiperBridge.GetItems(out _itemsNote);
                        }
                        if (GUI.Button(new Rect(x + 366, y, 50, 26), "复制"))
                        {
                            Flash(PiperBridge.CloneItem(lid, 1));
                            _items = PiperBridge.GetItems(out _itemsNote);
                        }
                        if (GUI.Button(new Rect(x + 420, y, 50, 26), "还原"))
                            Flash(PiperBridge.RestoreItemCount(lid));
                        if (GUI.Button(new Rect(x + 474, y, 52, 26), "清零"))
                        {
                            Flash(PiperBridge.SetItemCount(lid, 0));
                            _items = PiperBridge.GetItems(out _itemsNote);
                        }
                    }
                    y += RowH;
                }
                if (total == 0) { GUI.Label(new Rect(x, y, w, 22), "（无匹配物品）"); y += RowH; }
                if (GUI.Button(new Rect(x, y + 2, 56, 24), "上一页") && _page > 0) _page--;
                if (GUI.Button(new Rect(x + 62, y + 2, 56, 24), "下一页") && _page < maxPage - 1) _page++;
                if (!_showAll && GUI.Button(new Rect(x + 128, y + 2, 150, 24), $"全部物品+{StepName(_step)}"))
                    Flash(PiperBridge.SetAllItems((int)Math.Min(_step, int.MaxValue)));
                y += 34;
            }
        }

        // ============ 好感 ============
        if (_tab == 2)
        {
            GUI.Label(new Rect(x, y, 220, 22), "【NPC 好感】" + (_npcs == null ? "" : _npcs.Count + " 人"));
            if (GUI.Button(new Rect(x + w - 60, y, 60, 22), "刷新"))
                _npcs = PiperBridge.GetFavorNpcs(out _npcsNote);
            y += 24;
            if (_npcs == null)
            {
                GUI.Label(new Rect(x, y, w, 22), _npcsNote);
                y += RowH;
            }
            else
            {
                int maxPage = Math.Max(1, (_npcs.Count + Rows - 1) / Rows);
                if (_page >= maxPage) _page = maxPage - 1;
                if (_page < 0) _page = 0;
                GUI.Label(new Rect(x + w - 170, y - 24, 100, 22), $"{_page + 1}/{maxPage}页");
                int end = Math.Min(_npcs.Count, (_page + 1) * Rows);
                for (int i = _page * Rows; i < end; i++)
                {
                    var n = _npcs[i];
                    GUI.Label(new Rect(x, y + 4, 220, 22), $"{PiperNames.NpcName(n.NpcId)}  好感={n.FavorValue}");
                    int nid = n.NpcId;
                    if (GUI.Button(new Rect(x + 226, y, 86, 26), $"+{StepName(_step)}"))
                        Flash(PiperBridge.SetFavorNpc(nid, (int)Math.Min(_step, int.MaxValue)));
                    if (GUI.Button(new Rect(x + 316, y, 86, 26), $"−{StepName(_step)}"))
                        Flash(PiperBridge.SetFavorNpc(nid, 0));
                    y += RowH;
                }
                if (_npcs.Count == 0) { GUI.Label(new Rect(x, y, w, 22), "（无好感数据）"); y += RowH; }
                if (GUI.Button(new Rect(x, y + 2, 56, 24), "上一页") && _page > 0) _page--;
                if (GUI.Button(new Rect(x + 62, y + 2, 56, 24), "下一页") && _page < maxPage - 1) _page++;
                y += 38;
            }

            GUI.Label(new Rect(x, y, w - 130, 22), "名字解析: " + PiperNames.Status);
            if (GUI.Button(new Rect(x + w - 126, y, 126, 22), "重新解析名字"))
            {
                PiperNames.TryInit(true);
                Flash(PiperNames.Status);
            }
            y += 30;
            if (GUI.Button(new Rect(x, y, 150, 24), ManagerSweep.Done ? "再次扫描管理器" : "扫描管理器(连实时数据)"))
            {
                Flash(ManagerSweep.Sweep());
                if (ManagerSweep.Rl != null)
                {
                    PiperBridge.SetLiveRl(ManagerSweep.Rl);
                    Flash("✔ 管理器已连接，面板从此实时");
                }
            }
            y += 28;

            // ---- 势力声望 ----
            GUI.Label(new Rect(x, y, 220, 22), "【势力声望】");
            y += 24;
            try
            {
                var noteF2 = "";
                var fs = PiperBridge.GetFactions(out noteF2);
                if (fs.Count == 0) { GUI.Label(new Rect(x, y, w, 22), noteF2); y += RowH; }
                foreach (var f in fs)
                {
                    GUI.Label(new Rect(x, y + 4, 240, 22), $"{PiperBridge.FactionName(f.FactionId)}  等级={f.Level}  经验={f.Exp}");
                    int fid = f.FactionId;
                    if (GUI.Button(new Rect(x + 246, y, 80, 26), "等级+1"))
                        Flash(PiperBridge.AddFactionLevel(fid, 1));
                    if (GUI.Button(new Rect(x + 330, y, 80, 26), "领装饰"))
                        Flash(PiperBridge.ClaimFactionRewards(fid, true, false));
                    if (GUI.Button(new Rect(x + 414, y, 80, 26), "领信物"))
                        Flash(PiperBridge.ClaimFactionRewards(fid, false, true));
                    y += RowH;
                }
            }
            catch (Exception e) { GUI.Label(new Rect(x, y, w, 30), "势力区异常: " + e.Message); }
        }

        // ============ 员工 ============
        if (_tab == 3)
        {
            GUI.Label(new Rect(x, y, 260, 22), "【员工理智】" + (_staff == null ? "" : _staff.Count + " 人"));
            if (GUI.Button(new Rect(x + w - 176, y, 110, 22), "全部回满"))
            {
                Flash(PiperBridge.MaxAllStaffSan());
                _staff = PiperBridge.GetStaff(out _staffNote);
            }
            if (GUI.Button(new Rect(x + w - 60, y, 60, 22), "刷新"))
                _staff = PiperBridge.GetStaff(out _staffNote);
            y += 24;
            try
            {
                // 实时引擎状态 + 手动连接（自动链路未命中时一键补救）
                GUI.Label(new Rect(x, y, w - 232, 22), "理智: " + StaffLive.Status);
                if (GUI.Button(new Rect(x + w - 226, y, 106, 22), "连接运行时"))
                {
                    Flash(StaffLive.ForceConnect());
                    _staff = PiperBridge.GetStaff(out _staffNote);
                }
                if (GUI.Button(new Rect(x + w - 116, y, 116, 22), "机制透视"))
                    Flash(StaffLive.DumpMechanismNow());
                y += 26;
            }
            catch { }
            if (_staff == null)
            {
                GUI.Label(new Rect(x, y, w, 22), _staffNote);
                y += RowH;
            }
            else
            {
                int maxPage = Math.Max(1, (_staff.Count + Rows - 1) / Rows);
                if (_page >= maxPage) _page = maxPage - 1;
                if (_page < 0) _page = 0;
                GUI.Label(new Rect(x + w - 170, y - 24, 100, 22), $"{_page + 1}/{maxPage}页");
                int end = Math.Min(_staff.Count, (_page + 1) * Rows);
                for (int i = _page * Rows; i < end; i++)
                {
                    var s = _staff[i];
                    GUI.Label(new Rect(x, y + 4, 320, 22),
                        $"{s.Name}  理智={s.CurrentSan}/{s.MaxSan}" + (s.Live ? "  （实时）" : "  （存档值）"));
                    int idx = s.Idx;
                    int fmax = s.MaxSan;
                    if (GUI.Button(new Rect(x + 330, y, 80, 26), "回满"))
                    {
                        Flash(idx >= 0 ? PiperBridge.MaxStaffSan(idx) : StaffLive.SetRowFull(-1, fmax));
                        _staff = PiperBridge.GetStaff(out _staffNote);
                    }
                    y += RowH;
                }
                if (_staff.Count == 0) { GUI.Label(new Rect(x, y, w, 22), "（未雇员工）"); y += RowH; }
                if (GUI.Button(new Rect(x, y + 2, 56, 24), "上一页") && _page > 0) _page--;
                if (GUI.Button(new Rect(x + 62, y + 2, 56, 24), "下一页") && _page < maxPage - 1) _page++;
                y += 34;
            }
        }

        // ============ 建筑/家具 + 图鉴 ============
        if (_tab == 4)
        {
            // ---- 图鉴解锁区 ----
            try
            {
                int fin, force;
                PiperBridge.IllustrationStatus(out fin, out force);
                GUI.Label(new Rect(x, y, 340, 22), $"【图鉴】普通 {fin} 条 / 强制 {force} 条");
                if (GUI.Button(new Rect(x + w - 170, y, 170, 24), "图鉴全部解锁"))
                {
                    Flash(PiperBridge.UnlockAllIllustrations());
                    _blds = null; // 解锁可能联动建筑拥有状态，重载
                }
                y += 28;
            }
            catch (Exception e) { GUI.Label(new Rect(x, y, w, 22), "图鉴区异常: " + e.Message); y += 28; }

            // ---- 建筑列表头 ----
            int ownedN = _blds == null ? 0 : _blds.FindAll(b => b.Owned).Count;
            GUI.Label(new Rect(x, y, 320, 22), _blds == null ? "【建筑】" : $"【建筑】{_blds.Count} 种 / 已拥有 {ownedN}");
            if (GUI.Button(new Rect(x + w - 60, y, 60, 22), "刷新"))
                _blds = PiperBridge.GetBuildings(out _bldsNote);
            y += 24;

            // 筛选 + 搜索框提示
            string[] fb = { "全部", "未拥有", "已拥有" };
            for (int i = 0; i < fb.Length; i++)
            {
                bool sel = _bFilter == i;
                if (GUI.Button(new Rect(x + i * 64, y, 60, 22), sel ? "[" + fb[i] + "]" : fb[i]))
                    _bFilter = i;
            }
            if (UguiInput.Active)
                GUI.Label(new Rect(x + 200, y + 2, 220, 22), "搜索→右侧框（按名字过滤）");
            y += 26;

            if (_blds == null)
            {
                GUI.Label(new Rect(x, y, w, 22), string.IsNullOrEmpty(_bldsNote) ? "（点[刷新]加载建筑表）" : _bldsNote);
                y += RowH;
            }
            else
            {
                // 过滤（分类 + 名字搜索共享 _search）
                var shown = new List<PiperBridge.BuildingRow>();
                foreach (var b in _blds)
                {
                    if (_bFilter == 1 && b.Owned) continue;
                    if (_bFilter == 2 && !b.Owned) continue;
                    if (!string.IsNullOrEmpty(_search) && b.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    shown.Add(b);
                }
                int total = shown.Count;
                int maxPage = Math.Max(1, (total + Rows - 1) / Rows);
                if (_page >= maxPage) _page = maxPage - 1;
                if (_page < 0) _page = 0;
                GUI.Label(new Rect(x + w - 170, y - 2, 100, 22), $"{_page + 1}/{maxPage}页");
                int end = Math.Min(total, (_page + 1) * Rows);
                for (int i = _page * Rows; i < end; i++)
                {
                    var b = shown[i];
                    GUI.Label(new Rect(x, y + 4, 320, 22), (b.Owned ? "✓ " : "　") + b.Name + $"（{b.Id}）");
                    if (!b.Owned)
                    {
                        if (GUI.Button(new Rect(x + w - 100, y, 100, 26), "获得"))
                        {
                            Flash(PiperBridge.AddBuilding(b.Id));
                            _blds = PiperBridge.GetBuildings(out _bldsNote);
                        }
                    }
                    else
                    {
                        if (GUI.Button(new Rect(x + w - 104, y, 50, 26), $"−{StepName(_step)}"))
                        {
                            Flash(PiperBridge.ReduceBuilding(b.Id, (int)Math.Min(_step, int.MaxValue)));
                            _blds = PiperBridge.GetBuildings(out _bldsNote);
                        }
                        if (GUI.Button(new Rect(x + w - 50, y, 50, 26), "销毁"))
                        {
                            Flash(PiperBridge.DestroyBuilding(b.Id));
                            _blds = PiperBridge.GetBuildings(out _bldsNote);
                        }
                    }
                    y += RowH;
                }
                if (total == 0) { GUI.Label(new Rect(x, y, w, 22), "（无匹配建筑）"); y += RowH; }
                if (GUI.Button(new Rect(x, y + 2, 56, 24), "上一页") && _page > 0) _page--;
                if (GUI.Button(new Rect(x + 62, y + 2, 56, 24), "下一页") && _page < maxPage - 1) _page++;
                y += 34;
            }
        }

        // ---- 导出 ----
        if (GUI.Button(new Rect(x, _rect.y + _rect.height - 60, 150, 24), "导出诊断日志"))
            Flash(PiperBridge.ExportAll());

        // ---- 状态栏 ----
        string stat = _status;
        if (stat.Length > 0 && Time.realtimeSinceStartup - _statusTime > 6) stat = "";
        GUI.Label(new Rect(x, _rect.y + _rect.height - 30, w, 22), stat);
    }
}
