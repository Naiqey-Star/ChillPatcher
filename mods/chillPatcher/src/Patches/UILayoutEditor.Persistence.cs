using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
namespace ChillPatcher.Patches
{
    public sealed partial class UILayoutEditor
    {
        [Serializable] sealed class LayoutData { public int version = 1; public List<LayoutEntry> entries = new List<LayoutEntry>(); }
        [Serializable] sealed class LayoutEntry { public string key; public float x, y; }
        string SavePath => Path.Combine(BepInEx.Paths.ConfigPath, "ChillPatcher.UILayout.json");
        void Save()
        {
            try
            {
                var data = new LayoutData();
                foreach (var pair in positions) data.entries.Add(new LayoutEntry { key = pair.Key, x = pair.Value.x, y = pair.Value.y });
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
                var temporary = SavePath + ".tmp";
                File.WriteAllText(temporary, Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented));
                if (File.Exists(SavePath)) File.Replace(temporary, SavePath, SavePath + ".bak");
                else File.Move(temporary, SavePath);
                SetStatus("Layout saved: " + SavePath, "布局已保存：" + SavePath);
            }
            catch (Exception ex) { SetStatus("Save failed: " + ex.Message, "保存失败：" + ex.Message); }
        }
        void Load()
        {
            try
            {
                if (!File.Exists(SavePath)) { SetStatus("No saved layout.", "没有已保存的布局。"); return; }
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<LayoutData>(File.ReadAllText(SavePath));
                ValidateAndLoad(data);
            }
            catch (Exception ex) { SetStatus("Load failed: " + ex.Message, "加载失败：" + ex.Message); }
        }
    }
}
