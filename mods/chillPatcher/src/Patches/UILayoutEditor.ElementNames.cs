using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine.UI;

namespace ChillPatcher.Patches
{
    public sealed partial class UILayoutEditor
    {
        // Exact names observed in source take precedence over conservative word fallback.
        static readonly Dictionary<string, string> ElementChineseNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "IconExit_Button", "退出按钮" },
                { "IconStory_Button", "故事按钮" },
                { "IconCollaboToAlterEgo_Button", "ALTER EGO 联动按钮" },
                { "IconDecoration_Button", "装饰按钮" },
                { "IconEnviroment_Button", "环境按钮" },
                { "IconMusicPlaylist_Button", "音乐播放列表按钮" },
                { "PlayListMusicPlayButton", "播放列表音乐播放按钮" },
                { "LocalMusicImportButton", "导入本地音乐按钮" },
                { "RemoveButton", "移除按钮" },
                { "ChillPatcher_AddToQueue", "加入播放队列" },
                { "ChillPatcher_PlayNext", "下一首播放" },
                { "PlayQueueButton", "播放队列按钮" },
                { "TodoSwitchFinishButton", "切换待办完成状态按钮" },
                { "LongPressButton", "长按按钮" },
                { "UpButtonUI", "增加按钮" }
            };

        static readonly Dictionary<string, string> ElementNameWords =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Icon", "图标" }, { "Button", "按钮" }, { "Btn", "按钮" },
                { "UI", "界面" }, { "Music", "音乐" }, { "Playlist", "播放列表" },
                { "Play", "播放" }, { "List", "列表" }, { "Queue", "队列" },
                { "Local", "本地" }, { "Import", "导入" }, { "Remove", "移除" },
                { "Exit", "退出" }, { "Story", "故事" }, { "Decoration", "装饰" },
                { "Environment", "环境" }, { "Enviroment", "环境" },
                { "Todo", "待办" }, { "Switch", "切换" }, { "Finish", "完成" },
                { "Long", "长" }, { "Press", "按压" }, { "Up", "上" },
                { "Down", "下" }, { "Left", "左" }, { "Right", "右" },
                { "Next", "下一个" }, { "Previous", "上一个" },
                { "Contents", "内容" }, { "Container", "容器" },
                { "Panel", "面板" }, { "Setting", "设置" }, { "Settings", "设置" },
                { "Timer", "计时器" }, { "Pomodoro", "番茄钟" },
                { "Text", "文字" }, { "Image", "图像" }, { "Clone", "副本" },
                { "Simple", "简易" }, { "Rect", "矩形" }, { "Capsule", "胶囊形" },
                { "Prefab", "预制体" }, { "Wrapper", "包装容器" }
            };

        static string ChineseElementName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return "";
            string translated;
            if (ElementChineseNames.TryGetValue(objectName, out translated)) return translated;
            const string clone = "(Clone)";
            if (objectName.EndsWith(clone, StringComparison.OrdinalIgnoreCase))
                return ChineseElementName(objectName.Substring(0, objectName.Length - clone.Length)) + "（副本）";
            string words = Regex.Replace(objectName, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            words = Regex.Replace(words, "([a-z0-9])([A-Z])", "$1 $2");
            words = Regex.Replace(words, "[_\\s]+", " ");
            return Regex.Replace(words, "[A-Za-z]+", match =>
                ElementNameWords.TryGetValue(match.Value, out translated) ? translated : match.Value);
        }

        string LocalizedElementName(string objectName)
        {
            EnsureLanguage();
            return chinese ? ChineseElementName(objectName) : (objectName ?? "");
        }

        string ElementCaption(Button button)
        {
            if (!button) return "";
            var parent = button.transform.parent;
            return LocalizedElementName(button.name) + " - " +
                (parent ? LocalizedElementName(parent.name) : T("Root", "根节点"));
        }

        bool MatchesElementFilter(Button button, string key, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            if (!button) return false;
            query = query.Trim();
            string names = (key ?? "") + "\n" + button.name + "\n" + ChineseElementName(button.name);
            for (var parent = button.transform.parent; parent; parent = parent.parent)
                names += "\n" + parent.name + "\n" + ChineseElementName(parent.name);
            return names.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
