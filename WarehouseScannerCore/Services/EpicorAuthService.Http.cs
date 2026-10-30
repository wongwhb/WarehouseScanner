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
    /// <summary>HTTP 请求构建 / 发送 / JSON 解析辅助</summary>
    public partial class EpicorAuthService
    {
        /// <summary>
        /// 创建一个已带上所有标准 Header 的 HttpRequestMessage
        /// 
        /// 注意：这是无状态认证，每个请求都必须包含：
        /// 1. x-api-key: API 密钥
        /// 2. Authorization: Bearer {token}
        /// 3. CallSettings: 公司信息
        /// 4. license: License ID（仅写操作声明，只读查询使用共享 License 池）
        /// 
        /// 不需要任何 Cookie 或 Session 管理
        /// </summary>
        private HttpRequestMessage CreateRequest(HttpMethod method, string requestUri, string? jsonBody = null, bool requireLicense = true, string? bearerToken = null)
        {
            var request = new HttpRequestMessage(method, requestUri);

            // 1) API Key（必需）
            request.Headers.Add("x-api-key", _apiKey);

            // 2) CallSettings（使用预缓存 JSON）
            request.Headers.Add("CallSettings", _callSettingsJson);

            // 3) License — 仅写操作声明 ClaimedLicense，只读查询使用共享 License 池（不占用命名用户 License）
            if (requireLicense)
            {
                request.Headers.Add("license", _licenseJson);
            }

            // 4) Bearer Token（无状态认证核心）
            // 优先使用调用方在锁内取得的快照；未提供时（如已持锁的 Logout 内部调用）回退读取字段
            string? tokenToUse = bearerToken ?? _accessToken;
            if (!string.IsNullOrEmpty(tokenToUse))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenToUse);
            }

            // JSON Body
            if (!string.IsNullOrEmpty(jsonBody))
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            return request;
        }

        /// <summary>
        /// 统一发送请求（自动处理 Token 获取和 401 重试）
        /// 
        /// 根据 Epicor 文档：
        /// - "If the Bearer token expires, you must obtain a new token for continued API access"
        /// - 当收到 401 Unauthorized 时，自动刷新 Token 并重试请求
        /// - 不需要 Session 复用管理，每个请求独立携带 Token
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(
            HttpClient client,
            HttpMethod method,
            string requestUri,
            string? jsonBody = null,
            CancellationToken cancellationToken = default,
            bool requireLicense = true)
        {
            // 若无 Token 或 Token 已过期，先获取
            if (!await IsTokenValidAsync(cancellationToken))
            {
                if (!await RefreshTokenAsync(cancellationToken))
                {
                    // RefreshTokenAsync 已设置 LastApiError，直接返回一个代表失败的响应（不抛异常）
                    if (string.IsNullOrEmpty(LastApiError))
                        LastApiError = "无法获取 Epicor Token";
                    // 不设置 RequestMessage：HttpResponseMessage.Dispose 不会释放 RequestMessage，
                    // 为合成响应新建一个 HttpRequestMessage 会让它随响应一起变成无人释放的对象。
                    // 下游仅按状态码/LastApiError 判断（Program.cs 的 RequestMessage 判空已兼容）。
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
            }

            // 分层重试职责：本层只处理 401 → 刷新 Token；5xx / 连接层异常由 Program.cs 的 Polly 策略负责，
            // 二者触发条件不重叠，勿在此处叠加通用重试或修改 Polly 次数。
            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                // Token 在锁内取快照后再构造请求，避免读取到刷新过程中的中间状态
                string accessToken = await GetAccessTokenSnapshotAsync(cancellationToken);
                using var request = CreateRequest(method, requestUri, jsonBody, requireLicense, accessToken);
                var response = await client.SendAsync(request, cancellationToken);

                // Token 过期：根据 Epicor 文档，必须重新获取 Token
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < _maxRetries)
                {
                    _logger.LogWarning("收到 401 Unauthorized，Token 可能已过期，正在刷新...");
                    await ExpireTokenAsync(cancellationToken);
                    if (await RefreshTokenAsync(cancellationToken))
                    {
                        _logger.LogInformation("Token 刷新成功，重试请求");
                        response.Dispose(); // 401 响应已无用，释放响应体与底层连接
                        continue;
                    }
                    _logger.LogError("Token 刷新失败: {Error}", LastApiError);
                    return response;
                }

                return response;
            }

            // 达到最大重试次数 - 不抛异常，返回 LastApiError
            LastApiError = "请求失败：超出重试次数";
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        /// <summary>
        /// 发送并读取返回字符串
        /// </summary>
        private async Task<string> SendAndReadAsync(
            HttpClient client,
            HttpMethod method,
            string requestUri,
            string? jsonBody = null,
            CancellationToken cancellationToken = default,
            bool requireLicense = true)
        {
            using var response = await SendAsync(client, method, requestUri, jsonBody, cancellationToken, requireLicense);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            // 统一检查：HTTP 状态码 + Epicor 业务错误
            if (!response.IsSuccessStatusCode)
            {
                // HTTP 非成功状态码：先检查是否为 Epicor 标准错误格式
                if (!CheckEpicorApiError(body, requestUri, out _))
                {
                    // 非 Epicor 标准错误格式，使用 HTTP 状态信息
                    LastApiError = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                    _logger.LogError("请求失败 {StatusCode}: {Uri}\n{Error}", response.StatusCode, requestUri, body);
                }
            }
            else
            {
                // HTTP 成功状态码：检查是否包含 Epicor 业务错误
                if (!CheckEpicorApiError(body, requestUri, out _))
                {
                    ClearApiError();
                }
            }

            return body;
        }

        // ==================== 公开调用方法 ====================

        /// <summary>
        /// POST 调用 BO 服务（默认声明 License）
        /// </summary>
        /// <param name="requireLicense">true=声明 ClaimedLicense（写操作），false=使用共享 License 池（只读查询）</param>
        public async Task<string> PostBoJsonAsync(string url, string payload, CancellationToken cancellationToken = default, bool requireLicense = true)
        {
            using var client = CreateBoClient(HttpMethod.Post);
            return await SendAndReadAsync(client, HttpMethod.Post, url, payload, cancellationToken, requireLicense);
        }

        /// <summary>
        /// GET 调用 BO 服务（默认不声明 License，使用共享 License 池）
        /// </summary>
        public async Task<string> GetBoJsonAsync(string url, CancellationToken cancellationToken = default, bool requireLicense = false)
        {
            using var client = CreateBoClient(HttpMethod.Get);
            return await SendAndReadAsync(client, HttpMethod.Get, url, null, cancellationToken, requireLicense);
        }

        /// <summary>
        /// POST 调用 OData 服务（默认声明 License）
        /// </summary>
        /// <param name="requireLicense">true=声明 ClaimedLicense（写操作），false=使用共享 License 池（只读查询）</param>
        public async Task<string> PostODataJsonAsync(string url, string payload, CancellationToken cancellationToken = default, bool requireLicense = true)
        {
            using var client = CreateODataClient(HttpMethod.Post);
            return await SendAndReadAsync(client, HttpMethod.Post, url, payload, cancellationToken, requireLicense);
        }

        /// <summary>
        /// GET 调用 OData 服务（默认不声明 License，使用共享 License 池）
        /// </summary>
        public async Task<string> GetODataJsonAsync(string url, CancellationToken cancellationToken = default, bool requireLicense = false)
        {
            using var client = CreateODataClient(HttpMethod.Get);
            return await SendAndReadAsync(client, HttpMethod.Get, url, null, cancellationToken, requireLicense);
        }

        /// <summary>
        /// 检测 Epicor API 是否返回错误 JSON。
        /// 使用 EpicorJsonHelper.TryParseResponse 统一解析三种错误格式：
        /// 1. Epicor BO 标准格式 {HttpStatus, ErrorMessage}
        /// 2. RFC 9110 标准格式 {type, title, status, traceId}
        /// 3. 只有 ErrorMessage
        /// 若检测到错误，设置 LastApiError 属性，调用方应返回空列表/默认值。
        /// </summary>
        private bool CheckEpicorApiError(string body, string operationName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(body))
                return false;

            var (_, hasError, errMsg) = EpicorJsonHelper.TryParseResponse(body);
            if (hasError)
            {
                errorMessage = errMsg;
                LastApiError = errMsg;
                _logger.LogError("Epicor API 返回错误 - Operation:{Operation}, Error:{ErrorMessage}", operationName, errMsg);
                return true;
            }

            return false;
        }

        private (string character02, string shift, string shortChar01, string firstName, string lastName) ParseEmpBasicResponse(string json)
        {
            try
            {
                var jObj = JObject.Parse(json);
                var empBasicArr = jObj["returnObj"]?["EmpBasic"] as JArray;
                if (empBasicArr != null && empBasicArr.Count > 0)
                {
                    var empBasic = empBasicArr[0];
                    return (
                        empBasic.Value<string>("Character02") ?? "",
                        empBasic.Value<string>("Shift") ?? "",
                        empBasic.Value<string>("ShortChar01") ?? "",
                        empBasic.Value<string>("FirstName") ?? "",
                        empBasic.Value<string>("LastName") ?? ""
                    );
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "解析 EmpBasic 响应失败");
            }

            return ("error", "", "", "", "");
        }
    }
}