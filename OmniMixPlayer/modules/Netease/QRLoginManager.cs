using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using QRCoder;


namespace OmniMixPlayer.Module.Netease
{
    /// <summary>
    /// 二维码登录管理器
    /// 负责生成登录二维码、轮询登录状态
    /// </summary>
    /// <remarks>
    /// 原实现依赖原生 ChillNetease.dll 的 weapi 加密接口，该接口在网易云服务端
    /// 调整后拿不到 unikey，导致二维码一直加载不出来。这里改为直接调用网易云的
    /// 简单 HTTP 接口（/api/login/qrcode/*），登录成功后再把 MUSIC_U cookie 写回
    /// 原生桥接，其余播放/收藏仍走原生逻辑。
    /// </remarks>
    public class QRLoginManager
    {
        private readonly NeteaseBridge _bridge;
        private readonly ILogger _logger;

        private NeteaseBridge.QRLoginState _currentState;
        private byte[] _qrCodeBytes;
        private bool _isPolling;
        private CancellationTokenSource _pollingCts;
        private string _uniKey;
        private string _pendingMusicU;

        private static readonly TimeSpan POLLING_TIMEOUT = TimeSpan.FromMinutes(2);
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private const string UnikeyUrl = "https://music.163.com/api/login/qrcode/unikey?type=1";
        private const string CheckBaseUrl = "https://music.163.com/api/login/qrcode/client/login";
        private const string QRLoginUrlPrefix = "https://music.163.com/login?codekey=";

        public event Action OnLoginSuccess;
        public event Action<byte[]> OnQRCodeUpdated;
        public event Action<string> OnStatusChanged;
        public event Action<string> OnLoginFailed;

        public byte[] QRCodeBytes => _qrCodeBytes;
        public bool IsWaitingForLogin => _isPolling && _currentState != null;
        public string StatusMessage => _currentState?.StatusMsg ?? "未开始";

        public QRLoginManager(NeteaseBridge bridge, ILogger logger)
        {
            _bridge = bridge;
            _logger = logger;
        }

        public async Task<bool> StartLoginAsync()
        {
            try
            {
                CancelPolling();
                CleanupQRCodeResources();

                var unikey = await GetQRKeyAsync();
                if (string.IsNullOrEmpty(unikey))
                {
                    _logger.LogError("[QRLoginManager] 获取二维码 key 失败");
                    OnLoginFailed?.Invoke("获取二维码失败");
                    return false;
                }

                _uniKey = unikey;
                _currentState = new NeteaseBridge.QRLoginState
                {
                    UniKey = unikey,
                    QRCodeURL = QRLoginUrlPrefix + unikey,
                    StatusCode = 801,
                    StatusMsg = "等待扫码",
                };

                GenerateQRCodeBytes(_currentState.QRCodeURL);

                OnStatusChanged?.Invoke("请使用网易云音乐 APP 扫码");
                OnQRCodeUpdated?.Invoke(_qrCodeBytes);

                _pollingCts = new CancellationTokenSource();
                _isPolling = true;
                _ = PollLoginStatusAsync(_pollingCts.Token);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QRLoginManager] StartLoginAsync exception: {ex}");
                OnLoginFailed?.Invoke("启动登录失败: " + ex.Message);
                return false;
            }
        }

        private async Task<string> GetQRKeyAsync()
        {
            try
            {
                var body = await _http.GetStringAsync(UnikeyUrl).ConfigureAwait(false);
                var obj = JObject.Parse(body);
                if (obj["code"]?.Value<int>() != 200) return null;
                var key = obj["unikey"]?.Value<string>();
                return string.IsNullOrEmpty(key) ? null : key;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QRLoginManager] GetQRKeyAsync exception: {ex.Message}");
                return null;
            }
        }

        private async Task PollLoginStatusAsync(CancellationToken cancellationToken)
        {
            try
            {
                var startTime = DateTime.UtcNow;
                int failCount = 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (DateTime.UtcNow - startTime >= POLLING_TIMEOUT)
                    {
                        _logger.LogInformation("[QRLoginManager] 登录轮询超时，已停止");
                        _currentState = new NeteaseBridge.QRLoginState { StatusCode = -1, StatusMsg = "登录超时，请刷新二维码重试" };
                        OnStatusChanged?.Invoke(_currentState.StatusMsg);
                        OnLoginFailed?.Invoke(_currentState.StatusMsg);
                        return;
                    }

                    await Task.Delay(1500, cancellationToken).ConfigureAwait(false);

                    var status = await CheckQRStatusAsync().ConfigureAwait(false);
                    if (status == null)
                    {
                        failCount++;
                        _logger.LogWarning($"[QRLoginManager] 检查状态失败 ({failCount})");
                        if (failCount >= 60)
                        {
                            OnLoginFailed?.Invoke("检查登录状态失败，请刷新重试");
                            return;
                        }
                        continue;
                    }
                    failCount = 0;

                    _currentState = status;
                    OnStatusChanged?.Invoke(status.StatusMsg);

                    if (status.IsSuccess)
                    {
                        var ok = CompleteLogin();
                        _isPolling = false;
                        if (ok)
                        {
                            _logger.LogInformation("[QRLoginManager] 登录成功！");
                            OnLoginSuccess?.Invoke();
                        }
                        else
                        {
                            _logger.LogWarning("[QRLoginManager] 登录确认完成，但写入 cookie / 刷新用户信息失败");
                            OnLoginFailed?.Invoke("登录确认完成，但获取用户信息失败，请重试");
                        }
                        return;
                    }
                    else if (status.IsExpired)
                    {
                        OnStatusChanged?.Invoke("二维码已过期，请刷新重试");
                        OnLoginFailed?.Invoke("二维码已过期，请刷新重试");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[QRLoginManager] 轮询已取消");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QRLoginManager] 轮询异常: {ex}");
                OnLoginFailed?.Invoke("登录过程出错: " + ex.Message);
            }
            finally
            {
                _isPolling = false;
            }
        }

        private async Task<NeteaseBridge.QRLoginState> CheckQRStatusAsync()
        {
            try
            {
                using var resp = await _http.GetAsync($"{CheckBaseUrl}?key={_uniKey}&type=1").ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var obj = JObject.Parse(body);
                int code = obj["code"]?.Value<int>() ?? -1;
                string msg = obj["message"]?.Value<string>() ?? "";

                if (code == 803)
                {
                    // 登录成功后 cookie 可能出现在 body 的 cookie 字段，或 Set-Cookie 响应头
                    _pendingMusicU = ExtractCookieValue(obj["cookie"]?.Value<string>(), "MUSIC_U");
                    if (_pendingMusicU == null && resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    {
                        foreach (var c in setCookies)
                        {
                            _pendingMusicU = ExtractCookieValue(c, "MUSIC_U");
                            if (_pendingMusicU != null) break;
                        }
                    }
                }

                return new NeteaseBridge.QRLoginState
                {
                    UniKey = _uniKey,
                    QRCodeURL = "",
                    StatusCode = code,
                    StatusMsg = MapStatusMessage(code, msg),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QRLoginManager] CheckQRStatusAsync exception: {ex.Message}");
                return null;
            }
        }

        private bool CompleteLogin()
        {
            if (string.IsNullOrEmpty(_pendingMusicU))
            {
                _logger.LogWarning("[QRLoginManager] 803 响应中未找到 MUSIC_U cookie");
                return false;
            }

            if (!_bridge.SetCookie("MUSIC_U=" + _pendingMusicU))
            {
                _logger.LogWarning("[QRLoginManager] SetCookie 失败");
                return false;
            }

            var refresh = _bridge.RefreshLogin();
            if (refresh != NeteaseBridge.RefreshLoginResult.Success)
            {
                _logger.LogWarning($"[QRLoginManager] RefreshLogin 失败: {refresh}");
                return false;
            }

            return true;
        }

        private static string ExtractCookieValue(string cookieLine, string name)
        {
            if (string.IsNullOrEmpty(cookieLine)) return null;
            foreach (var rawPart in cookieLine.Split(';'))
            {
                var part = rawPart.Trim();
                if (part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = part.Substring(name.Length + 1).Trim();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }
            return null;
        }

        private static string MapStatusMessage(int code, string fallback)
        {
            switch (code)
            {
                case 800: return "二维码已失效";
                case 801: return "等待扫码";
                case 802: return "已扫码，等待确认";
                case 803: return "登录成功";
                default: return string.IsNullOrEmpty(fallback) ? ("未知状态: " + code) : fallback;
            }
        }

        public void CancelLogin()
        {
            CancelPolling();
            _currentState = null;
            _uniKey = null;
            CleanupQRCodeResources();
        }

        private void CancelPolling()
        {
            if (_pollingCts != null)
            {
                _pollingCts.Cancel();
                _pollingCts.Dispose();
                _pollingCts = null;
            }
            _isPolling = false;
        }

        private void CleanupQRCodeResources()
        {
            _qrCodeBytes = null;
            _pendingMusicU = null;
        }

        private void GenerateQRCodeBytes(string url)
        {
            try
            {
                using (var qrGenerator = new QRCodeGenerator())
                {
                    var qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                    using (var qrCode = new PngByteQRCode(qrCodeData))
                    {
                        _qrCodeBytes = qrCode.GetGraphic(10); // 10 像素每模块
                    }
                }

                _logger.LogInformation($"[QRLoginManager] 二维码生成成功: {_qrCodeBytes.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[QRLoginManager] 生成二维码失败: {ex}");
            }
        }
    }
}
