using System;
using UnityEngine;
using UnityEngine.UI;

namespace PiperTrainer;

// v4.3.0：IMGUI 的 TextField 在本游戏被 strip（TextEditor.set_text 缺失），
// 改为运行时创建 uGUI InputField（游戏自己的 UI 体系，组件完好且支持输入法），
// 盖在修改器面板的搜索行位置，文本由面板每帧轮询同步。
public static class UguiInput
{
    public static InputField Field;
    public static GameObject Root;
    public static bool Failed;
    private static bool _selfTested;

    public static bool Active => Field != null;

    private static GameObject MakeUi(string name, Transform parent)
    {
        var go = new GameObject(name);
        try { go.AddComponent<RectTransform>(); }
        catch { }
        go.transform.SetParent(parent, false);
        return go;
    }

    public static bool Ensure()
    {
        if (Field != null) return true;
        if (Failed) return false;
        try
        {
            Root = new GameObject("PiperTrainerInputRoot");
            UnityEngine.Object.DontDestroyOnLoad(Root);
            var canvas = Root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;
            // uGUI 点击射线是逐 Canvas 派发的：没有 GraphicRaycaster 的画布不参与命中检测，
            // 表现为看得见但点不中、无法聚焦（v4.11 搜索框不可用的根因）
            Root.AddComponent<GraphicRaycaster>();

            var holder = MakeUi("field", Root.transform);
            var hrt = holder.GetComponent<RectTransform>();
            hrt.sizeDelta = new Vector2(220, 24);

            var img = holder.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.92f);

            Font font = null;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception fe) { PiperTrainerPlugin.Log2?.LogInfo("字体1失败: " + fe.Message); }
            if (font == null) { try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { } }
            PiperTrainerPlugin.Log2?.LogInfo("UGUI 输入框字体: " + (font == null ? "NULL!" : "ok"));

            var txtGo = MakeUi("text", holder.transform);
            var trt = txtGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(6, 1); trt.offsetMax = new Vector2(-6, -1);
            var txt = txtGo.AddComponent<Text>();
            txt.font = font; txt.color = Color.black; txt.fontSize = 14;
            txt.alignment = TextAnchor.MiddleLeft; txt.supportRichText = false;

            var phGo = MakeUi("ph", holder.transform);
            var prt = phGo.GetComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = new Vector2(6, 1); prt.offsetMax = new Vector2(-6, -1);
            var ph = phGo.AddComponent<Text>();
            ph.font = font; ph.color = new Color(0.45f, 0.45f, 0.45f, 0.9f); ph.fontSize = 14;
            ph.alignment = TextAnchor.MiddleLeft; ph.supportRichText = false; ph.text = "输入物品名搜索…";

            var f = holder.AddComponent<InputField>();
            f.textComponent = txt;
            f.placeholder = ph;
            f.targetGraphic = img;
            f.customCaretColor = true;
            f.caretColor = Color.black;
            f.characterLimit = 20;
            f.text = "";

            Field = f;
            Root.SetActive(false);
            PiperTrainerPlugin.Log2?.LogInfo("UGUI 搜索输入框创建成功（含 GraphicRaycaster）");
            PiperBridge.Toast("搜索框已就绪（背包页右侧）");
            return true;
        }
        catch (Exception e)
        {
            Failed = true;
            PiperTrainerPlugin.Log2?.LogInfo("UGUI 输入框创建失败: " + e);
            return false;
        }
    }

    // 自检：尝试写入一个字符验证 setter 可用（部分组件也可能被 strip）
    public static void SelfTest()
    {
        if (_selfTested) return;
        _selfTested = true;
        try
        {
            if (!Ensure()) return;
            var bak = Field.text;
            Field.text = "T"; // 触发 set_text 路径
            Field.text = bak;
            PiperTrainerPlugin.Log2?.LogInfo("UGUI 输入框自检通过");
        }
        catch (Exception e)
        {
            Failed = true;
            Field = null;
            PiperTrainerPlugin.Log2?.LogInfo("UGUI 输入框自检失败: " + e.Message);
        }
    }

    public static bool IsFocused()
    {
        try { return Field != null && Field.isFocused; } catch { return false; }
    }

    // 每帧同步：显隐 + 面板内定位（GUI 坐标 -> uGUI 屏幕坐标）
    public static void Sync(bool show, Rect guiRect)
    {
        try
        {
            if (!show)
            {
                if (Root != null && Root.activeSelf) Root.SetActive(false);
                return;
            }
            if (!Ensure()) return;
            if (!Root.activeSelf) Root.SetActive(true);
            var rt = Field.GetComponent<RectTransform>();
            float cx = guiRect.x + guiRect.width / 2f - Screen.width / 2f;
            float cy = Screen.height / 2f - (guiRect.y + guiRect.height / 2f);
            rt.anchoredPosition = new Vector2(cx, cy);
            rt.sizeDelta = new Vector2(guiRect.width, guiRect.height);
        }
        catch { }
    }
}
