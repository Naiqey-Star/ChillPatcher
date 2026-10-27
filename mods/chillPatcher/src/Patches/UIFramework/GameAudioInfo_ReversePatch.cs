using System;
using System.Threading;
using Bulbul;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace ChillPatcher.Patches.UIFramework
{
    /// <summary>
    /// 反向补丁：将 GameAudioInfo 的 private 方法暴露为 public
    /// </summary>
    [HarmonyPatch]
    public static class GameAudioInfo_ReversePatch
    {
        /// <summary>
        /// 反向补丁：暴露 DownloadAudioFile 方法
        /// </summary>
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(GameAudioInfo), "DownloadAudioFile")]
        public static UniTask<GameAudioInfo.LocalAudioLoadResult> DownloadAudioFile(string uri, CancellationToken ct)
        {
            // 这个方法体会被 Harmony 自动替换为原始方法的实现
            throw new NotImplementedException("This is a stub for Harmony reverse patch");
        }
    }
}
