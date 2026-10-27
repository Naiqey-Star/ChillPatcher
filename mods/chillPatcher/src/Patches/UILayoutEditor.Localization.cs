using System;
using System.IO;
using UnityEngine;
namespace ChillPatcher.Patches
{
    public sealed partial class UILayoutEditor
    {
        bool languageLoaded, chinese = true, fontAttempted;
        Font editorFont;
        GUISkin editorSkin, sourceSkin;
        string statusEnglish = "", statusChinese = "";
        string LanguagePath => Path.Combine(BepInEx.Paths.ConfigPath, "ChillPatcher.UILayout.language.txt");
        string T(string en, string zh) { EnsureLanguage(); return chinese ? zh : en; }
        void EnsureLanguage()
        {
            if (languageLoaded) return;
            languageLoaded = true;
            try { if (File.Exists(LanguagePath)) chinese = File.ReadAllText(LanguagePath).Trim() != "en"; }
            catch (Exception) { SetStatus("Cannot read language preference.", "无法读取语言偏好设置。"); }
        }
        void SetStatus(string en, string zh)
        {
            statusEnglish = en; statusChinese = zh;
            status = chinese ? zh : en;
        }
        string LocalizedStatus => string.IsNullOrEmpty(statusEnglish) ? status : T(statusEnglish, statusChinese);
        void ToggleLanguage()
        {
            EnsureLanguage(); chinese = !chinese;
            try
            {
                Directory.CreateDirectory(BepInEx.Paths.ConfigPath);
                File.WriteAllText(LanguagePath, chinese ? "zh-CN" : "en");
            }
            catch (Exception) { SetStatus("Cannot save language preference.", "无法保存语言偏好设置。"); }
            InvalidateEditorList();
        }
        GUISkin LocalizedSkin()
        {
            EnsureLanguage();
            if (!fontAttempted)
            {
                fontAttempted = true;
                try { editorFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial Unicode MS" }, 16); }
                catch (Exception) { }
            }
            if (!editorSkin || sourceSkin != GUI.skin)
            {
                if (editorSkin) Destroy(editorSkin);
                sourceSkin = GUI.skin;
                editorSkin = Instantiate(sourceSkin);
                if (editorFont)
                {
                    editorSkin.font = editorFont;
                    foreach (var s in new[] { editorSkin.label, editorSkin.button, editorSkin.textField, editorSkin.window, editorSkin.box, editorSkin.toggle }) s.font = editorFont;
                }
                listStyle = null;
            }
            return editorSkin;
        }
        void ReleaseLocalization()
        {
            if (editorSkin) Destroy(editorSkin);
            if (editorFont) Destroy(editorFont);
        }
    }
}
