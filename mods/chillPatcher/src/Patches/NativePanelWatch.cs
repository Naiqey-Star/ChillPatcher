using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChillPatcher.Patches
{
    /// <summary>
    /// 只读读取游戏原生操作面板的开关状态（歌曲列表 / 日历 / 笔记 / 待办 / 设置等），
    /// 供叠加面板自动避让。全部复用游戏自带的 IsActive()/IsActive 状态，不接管也不改变
    /// 原生面板的开关逻辑；组件实例按类型缓存，场景切换后缓存失效时才重新扫描。
    /// </summary>
    internal static class NativePanelWatch
    {
        private const float PollInterval = 0.1f;

        private sealed class Probe
        {
            public Component Target;
            public Func<bool> Read;
            public string Name;
        }

        private static readonly List<Probe> probes = new List<Probe>();
        private static float nextPoll;
        private static float nextRescan;
        private static int missingTypes;

        /// <summary>是否任意原生面板处于打开状态。</summary>
        internal static bool AnyOpen { get; private set; }

        /// <summary>当前打开的原生面板（用于日志，未打开时为空）。</summary>
        internal static string OpenPanel { get; private set; } = "";

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + PollInterval;

            bool stale = probes.Count == 0;
            for (int i = 0; i < probes.Count && !stale; i++)
                if (probes[i].Target == null) stale = true;
            // 某些面板组件可能在场景加载后才创建；缺类型时隔几秒补扫一次
            if (!stale && missingTypes > 0 && now >= nextRescan) stale = true;
            if (stale) Rebuild();

            string first = "";
            bool any = false;
            for (int i = 0; i < probes.Count; i++)
            {
                var p = probes[i];
                if (p.Target == null) continue;
                bool open;
                try { open = p.Read(); }
                catch { open = false; }
                if (!open) continue;
                any = true;
                if (first.Length == 0) first = p.Name;
            }

            AnyOpen = any;
            OpenPanel = first;
        }

        private static void Rebuild()
        {
            probes.Clear();
            missingTypes = 0;
            nextRescan = Time.unscaledTime + 5f;
            // 每个面板都读取游戏自己的活动状态，这里只做汇总
            Add<MusicUI>("歌曲列表", c => c.IsActivePlayList());
            Add<Bulbul.FacilityMusic>("音乐设施", c => c.IsActive != null && c.IsActive.CurrentValue);
            Add<Bulbul.FacilityCalendar>("日历", c => c.IsActive());
            Add<FacilityNote>("笔记", c => c.IsActive());
            Add<Bulbul.FacilityTodo>("待办", c => c.IsActive());
            Add<Bulbul.FacilitySetting>("设置", c => c.IsActive());
            Add<FacilityDecoration>("装饰", c => c.IsActive != null && c.IsActive.CurrentValue);
            Add<Bulbul.FacilityStatistics>("统计", c => c.IsActive());
            Add<Bulbul.FacilityHabitTracker>("习惯", c => c.IsActive());
            Add<Bulbul.FacilityProfile>("档案", c => c.IsActive());
            Add<Bulbul.FacilityEnvironment>("环境", c => c.IsActive != null && c.IsActive.CurrentValue);
        }

        private static void Add<T>(string name, Func<T, bool> read) where T : Component
        {
            var found = UnityEngine.Object.FindObjectsOfType(typeof(T), true);
            bool any = false;
            foreach (var obj in found)
            {
                var comp = obj as T;
                if (comp == null) continue;
                any = true;
                probes.Add(new Probe
                {
                    Target = comp,
                    Name = name,
                    Read = () => read(comp)
                });
            }
            if (!any) missingTypes++;
        }
    }
}
