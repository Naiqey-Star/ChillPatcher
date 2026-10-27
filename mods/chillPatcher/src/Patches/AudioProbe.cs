using System.Text;
using UnityEngine;

namespace ChillPatcher.Patches
{
    // 诊断：定期打印非音乐流 AudioSource 的状态，用于定位 AIChat TTS 无声。
    internal sealed class AudioProbe : MonoBehaviour
    {
        private static AudioProbe _instance;
        private float _nextAt;
        private static readonly bool Enabled =
            System.Environment.GetEnvironmentVariable("CHILL_AUDIO_PROBE") == "1";
        private readonly StringBuilder _sb = new StringBuilder();

        public static void Ensure()
        {
            if (!Enabled) return;
            if (_instance != null) return;
            var host = new GameObject("ChillPatcher_AudioProbe");
            Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<AudioProbe>();
        }

        private static float Rms(AudioSource a)
        {
            try
            {
                var clip = a.clip;
                int n = Mathf.Min(4096, clip.samples * clip.channels);
                if (n <= 0) return -1f;
                var buf = new float[n];
                if (!clip.GetData(buf, 0)) return -2f;
                double sum = 0;
                for (int i = 0; i < n; i++) sum += buf[i] * buf[i];
                return (float)Mathf.Sqrt((float)(sum / n));
            }
            catch { return -3f; }
        }

        private void Update()
        {
            // 默认关闭：每 2 秒遍历全部 AudioSource 并读取 PCM，
            // 既刷日志又会在音频线程上造成周期性卡顿。
            if (!Enabled) return;
            if (Time.unscaledTime < _nextAt) return;
            _nextAt = Time.unscaledTime + 2f;
            _sb.Length = 0;
            _sb.Append("[AudioProbe]");
            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<AudioSource>();
            for (int i = 0; i < all.Length && n < 12; i++)
            {
                var a = all[i];
                if (a == null || a.clip == null) continue;
                if (a.clip.name.StartsWith("pcm_stream_")) continue;
                if (a.gameObject.name.IndexOf("Ambient", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                n++;
                _sb.Append(" {").Append(a.gameObject.name)
                   .Append(" clip=").Append(a.clip.name)
                   .Append(" len=").Append(a.clip.length.ToString("F1"))
                   .Append(" vol=").Append(a.volume.ToString("F2"))
                   .Append(" mute=").Append(a.mute)
                   .Append(" play=").Append(a.isPlaying)
                   .Append(" load=").Append(a.clip.loadState)
                   .Append(" mixer=").Append(a.outputAudioMixerGroup != null ? a.outputAudioMixerGroup.name : "none")
                   .Append(" spatial=").Append(a.spatialBlend.ToString("F2"))
                   .Append(" listener=").Append(AudioListener.volume.ToString("F2"))
                   .Append(" rms=").Append(Rms(a).ToString("F4"))
                   .Append('}');
            }
            if (n > 0) Plugin.Log.LogInfo(_sb.ToString());
        }
    }
}
