using System;
using System.Collections.Generic;
using BepInEx.Logging;
using ChillPatcher.Integration;

namespace ChillPatcher.JSApi
{
    /// <summary>
    /// 原生日记联动的 JS 接口（供 UI 调试或手动触发）。
    ///
    /// 示例：
    ///   const d = JSON.parse(chill.diary.getToday())
    ///   chill.diary.setToday("今天写了 2000 字")
    ///   chill.diary.regenerate()
    ///   JSON.parse(chill.diary.getStatus())
    /// </summary>
    public class ChillDiaryApi
    {
        private readonly ManualLogSource _logger;

        public ChillDiaryApi(ManualLogSource logger)
        {
            _logger = logger;
        }

        public string getToday()
        {
            var payload = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["date"] = DateTime.Now.Date.ToString("yyyy-MM-dd"),
                ["text"] = AutoDiaryService.GetTodayText()
            };
            return JSApiHelper.ToJson(payload);
        }

        public string setToday(string text)
        {
            var ok = AutoDiaryService.SetTodayText(text, out var error);
            return ToResult(ok, error);
        }

        public string regenerate()
        {
            var ok = AutoDiaryService.RequestRegenerate(out var error);
            return ToResult(ok, error);
        }

        public string getStatus()
        {
            return AutoDiaryService.GetStatusJson();
        }

        private static string ToResult(bool ok, string error)
        {
            var payload = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["error"] = ok ? string.Empty : (error ?? string.Empty)
            };
            return JSApiHelper.ToJson(payload);
        }
    }
}
