using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WarehouseScannerCore.Configuration;
using WarehouseScannerCore.Utilities;
namespace WarehouseScanner.Services
{
    /// <summary>Token 管理 / 登录 / 注销</summary>
    public partial class EpicorAuthService
    {

        // ==================== Token 管理核心 ====================

        /// <summary>
        /// 无锁读取 Token 有效性（仅限已持有 _tokenLock 的调用方使用）。
        /// </summary>
        private bool IsTokenValidNoLock()
        {
            return !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiryTime;
        }

        /// <summary>异步读取 Token 有效性快照（不阻塞请求线程）</summary>
        private async Task<bool> IsTokenValidAsync(CancellationToken cancellationToken)
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                return IsTokenValidNoLock();
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// 异步读取当前 AccessToken 快照（供构造请求头使用，避免读取中间状态）。
        /// </summary>
        private async Task<string> GetAccessTokenSnapshotAsync(CancellationToken cancellationToken)
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                return _accessToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// 仅将过期时间置为失效（保留 access/refresh token），强制下次刷新优先走 refresh_token。
        /// </summary>
        private async Task ExpireTokenAsync(CancellationToken cancellationToken)
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                _tokenExpiryTime = DateTime.MinValue;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// 获取新 Token（无状态认证）
        /// 
        /// 根据 Epicor 文档：
        /// - "In order to use token authentication you generate a bearer token in the first interaction with the API"
        /// - "For subsequent interactions the token is used as the authentication method instead of username and password"
        /// - Token 有效期可在 Epicor Administration Console 配置（1-24小时）
        /// 
        /// Epicor License 规则：每次 grant_type=password 登录都创建新 Session 并占用 1 个 License。
        /// 使用 grant_type=refresh_token 刷新 Token 不会创建新 Session，不额外占用 License。
        /// 当必须使用 password 重登时，先调用 SessionModSvc/Logout 释放旧 Session 以避免 License 叠加。
        /// 
        /// 并发：整个刷新流程（含读取/写入三个 Token 字段、Logout、password 重登）都在 _tokenLock 内，
        /// 与 LogoutAsync 使用同一把锁，避免读到半更新状态或互相覆盖。
        /// </summary>
        private async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                // 未过期直接返回（锁内判断，避免与刷新/登出交错）
                if (IsTokenValidNoLock())
                {
                    _logger.LogDebug("Token 仍然有效，跳过刷新");
                    return true;
                }

                // ========== 优先策略：使用 refresh_token 刷新（不创建新 Session，不额外占用 License） ==========
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    _logger.LogInformation("尝试使用 refresh_token 刷新 Token - 用户: {UserId}", _userId);
                    bool refreshed = await TryRefreshTokenAsync(cancellationToken);
                    if (refreshed)
                        return true;

                    // refresh_token 失败（可能已过期或被撤销），清除后回退到 password
                    _logger.LogWarning("refresh_token 刷新失败，回退到 password 模式 - 用户: {UserId}", _userId);
                    _refreshToken = string.Empty;
                }

                // ========== 回退策略：先 Logout 旧 Session 释放 License，再用 password 重登 ==========
                if (!string.IsNullOrEmpty(_accessToken))
                {
                    _logger.LogInformation("password 重登前，先 Logout 释放旧 Session/License - 用户: {UserId}", _userId);
                    try
                    {
                        await LogoutCoreAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // Logout 失败不应阻止重登，旧 Session 将依赖 Epicor 超时自动回收
                        _logger.LogWarning(ex, "Logout 旧 Session 失败（非致命），继续 password 重登 - 用户: {UserId}", _userId);
                    }
                }

                return await PasswordLoginAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // 不透传 ex.Message（可能含内部主机名/URL），详细异常由下方日志记录
                LastApiError = "刷新 Token 失败（网络或服务异常）";
                _logger.LogError(ex, "刷新 Token 异常");
                return false;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// 使用 refresh_token 刷新 Access Token（不创建新 Session，不额外占用 License）
        /// Epicor REST v2 支持 OAuth2 标准 grant_type=refresh_token（10.2.400+）
        /// </summary>
        private async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
        {
            try
            {
                var parameters = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = _refreshToken
                };
                using var content = new FormUrlEncodedContent(parameters);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

                using var tokenClient = CreateTokenClient();
                tokenClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
                tokenClient.DefaultRequestHeaders.Add("Accept", "application/json");

                using var response = await tokenClient.PostAsync(_tokenUrl, content, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("refresh_token 刷新失败 (Status={StatusCode}) - 用户: {UserId}",
                        (int)response.StatusCode, _userId);
                    return false;
                }

                var tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(responseBody);
                if (tokenResponse?.AccessToken == null)
                {
                    _logger.LogWarning("refresh_token 响应格式异常 - 用户: {UserId}", _userId);
                    return false;
                }

                _accessToken = tokenResponse.AccessToken;
                if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
                    _refreshToken = tokenResponse.RefreshToken;

                int expiresInSeconds = tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : _tokenExpiryMinutes * 60;
                _tokenExpiryTime = DateTime.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - (_tokenRefreshAheadMinutes * 60)));

                _logger.LogInformation("refresh_token 刷新成功（不创建新 Session，不额外占用 License），有效期至 {ExpiryTime} (UTC)",
                    _tokenExpiryTime);
                ClearApiError();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "refresh_token 刷新异常 - 用户: {UserId}", _userId);
                return false;
            }
        }

        /// <summary>
        /// 使用 grant_type=password 登录（创建新 Session，占用 1 License）
        /// 调用前应先 Logout 旧 Session 以避免 License 叠加
        /// </summary>
        private async Task<bool> PasswordLoginAsync(CancellationToken cancellationToken)
        {
            var parameters = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = _userId,
                ["password"] = _password,
                ["company"] = _company
            };
            using var content = new FormUrlEncodedContent(parameters);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

            using var tokenClient = CreateTokenClient();
            tokenClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
            tokenClient.DefaultRequestHeaders.Add("Accept", "application/json");

            _logger.LogInformation("正在获取 Epicor Token (password): Url={TokenUrl}, Username={Username}, Company={Company}",
                _tokenUrl, _userId, _company);

            using var response = await tokenClient.PostAsync(_tokenUrl, content, cancellationToken);
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var (_, _, errorMsg) = EpicorJsonHelper.TryParseResponse(responseBody);
                LastApiError = string.IsNullOrWhiteSpace(errorMsg) || errorMsg == "响应数据为空"
                    ? $"获取 Token 失败 (Status={(int)response.StatusCode})"
                    : errorMsg;

                _logger.LogError("获取 Token 失败 (Status={StatusCode}, Username={Username}, Company={Company}): {Error}",
                    (int)response.StatusCode, _userId, _company, LastApiError);
                return false;
            }

            var tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(responseBody);
            if (tokenResponse?.AccessToken == null)
            {
                LastApiError = "Token 响应格式异常";
                // 安全：响应体可能含 access_token/refresh_token，截断到 200 字符防止凭据完整入日志
                var truncatedBody = responseBody != null && responseBody.Length > 200
                    ? responseBody.Substring(0, 200) + "…[已截断]"
                    : responseBody;
                _logger.LogError("Token 响应格式异常: {Body}", truncatedBody);
                return false;
            }

            _accessToken = tokenResponse.AccessToken;
            // 保存 refresh_token（如果 Epicor 返回）
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                _refreshToken = tokenResponse.RefreshToken;
                _logger.LogInformation("Epicor 返回 refresh_token，后续刷新将优先使用（不创建新 Session）");
            }
            else
            {
                _logger.LogInformation("Epicor 未返回 refresh_token，后续刷新将使用 password + Logout 模式");
            }

            int expiresInSeconds = tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : _tokenExpiryMinutes * 60;
            _tokenExpiryTime = DateTime.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - (_tokenRefreshAheadMinutes * 60)));

            _logger.LogInformation("Token 获取成功（新 Session，占用 1 License），有效期至 {ExpiryTime} (UTC)", _tokenExpiryTime);
            ClearApiError();
            return true;
        }

        /// <summary>
        /// 公开的获取 Token 方法
        /// </summary>
        public async Task<bool> GetEpicorToken() => await RefreshTokenAsync();

        /// <summary>
        /// 调用 Epicor SessionModSvc/Logout 释放服务端 Session/License
        /// 
        /// Epicor 规则：每次登录（TokenResource.svc grant_type=password）创建 1 个 Session + 占用 1 个 License。
        /// 显式 Logout 可立即释放该 Session 及 License，而非等待 Epicor 服务端超时自动回收（通常 20-30 分钟）。
        /// 
        /// 公开方法：用于用户主动登出时调用（自行获取 _tokenLock）。
        /// 内部方法：RefreshTokenAsync 在 password 重登前自动调用 LogoutCoreAsync（此时锁已持有）。
        /// </summary>
        public async Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                await LogoutCoreAsync(cancellationToken);
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>
        /// Logout 核心实现。
        /// 前置条件：调用方必须已持有 _tokenLock（RefreshTokenAsync / LogoutAsync 均满足），
        /// 因此此处不得再取锁，否则 SemaphoreSlim 非重入会导致死锁。
        /// </summary>
        private async Task LogoutCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!string.IsNullOrEmpty(_accessToken))
                {
                    using var client = CreateBoClient(HttpMethod.Post);
                    // Epicor BO 方法要求 POST 带 JSON body（无参方法传 {}），空 body 会返回 400
                    using var request = CreateRequest(HttpMethod.Post, "Ice.BO.SessionModSvc/Logout", "{}", requireLicense: false, bearerToken: _accessToken);
                    using var response = await client.SendAsync(request, cancellationToken);

                    if (response.IsSuccessStatusCode)
                        _logger.LogInformation("Epicor Session Logout 成功（释放 License） - 用户: {UserId}", _userId);
                    else
                        _logger.LogWarning("Epicor Session Logout 失败 (Status={StatusCode}) - 用户: {UserId}",
                            (int)response.StatusCode, _userId);
                }
            }
            catch (Exception ex)
            {
                // Logout 失败不应阻塞业务流程，记录警告即可
                _logger.LogWarning(ex, "Epicor Session Logout 异常（非致命） - 用户: {UserId}", _userId);
            }
            finally
            {
                // 无论如何清除本地 Token 状态（仍在 _tokenLock 内，不会抹掉并发刷新的结果）
                _accessToken = string.Empty;
                _refreshToken = string.Empty;
                _tokenExpiryTime = DateTime.MinValue;
            }
        }

    }
}