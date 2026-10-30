using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
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
    /// <summary>
    /// Epicor API 服务 - Bearer Token + x-api-key 认证
    /// 
    /// 认证特性：
    /// - 每次调用 TokenResource.svc (grant_type=password) 都会在 Epicor 端创建一个新 Session，占用 1 个 License
    /// - 使用 grant_type=refresh_token 刷新 Token 不会创建新 Session，不额外占用 License
    /// - Token 过期后优先使用 refresh_token 刷新；若无 refresh_token 则先 Logout 旧 Session 再 password 重登
    /// - 支持 x-api-key + Bearer Token 双重认证
    /// 
    /// License 优化策略：
    /// - refresh_token 刷新 → 不创建新 Session → 不额外占用 License
    /// - password 重登前先 Logout → 释放旧 Session → 同一用户始终 1 License
    /// </summary>
    public partial class EpicorAuthService : IDisposable
    {
        /// <summary>命名 HttpClient：请求可能包含写操作（POST），连接被重置时不重试（防重复过账）</summary>
        private const string WriteClientName = "EpicorApi";

        /// <summary>命名 HttpClient：只读/幂等调用（GET、Token 获取），连接层异常可安全重试</summary>
        private const string IdempotentClientName = "EpicorApiIdempotent";

        // IHttpClientFactory — 由 DI 管理底层 HttpMessageHandler 连接池
        // 每次调用 CreateClient() 返回轻量包装器，用完即弃
        private readonly IHttpClientFactory _httpClientFactory;

        private readonly string _userId;
        private readonly string _password;
        private readonly string _tokenUrl;
        private readonly string _apiKey;
        private readonly string _licenseId;
        private readonly string _company;
        private readonly string _plant;
        private readonly string _baseUrl;

        // 预缓存不变的 Header JSON，避免每次请求重复序列化
        private readonly string _callSettingsJson;
        private readonly string _licenseJson;

        // 从 EpicorSettings 读取的运行时配置
        private readonly int _timeoutSeconds;
        private readonly int _maxRetries;
        private readonly int _tokenExpiryMinutes;
        private readonly int _tokenRefreshAheadMinutes;
        private const int LargePageSize = 1000;

        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private readonly ILogger _logger;

        private string _accessToken = "";
        private string _refreshToken = "";
        private DateTime _tokenExpiryTime = DateTime.MinValue;

        private readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// 指示最近一次 Epicor API 调用是否返回错误（非 HTTP 200 或包含 ErrorMessage）。
        /// 调用业务方法前可通过此属性判断是否需要中断后续业务逻辑。
        /// </summary>
        public bool HasApiError => !string.IsNullOrEmpty(LastApiError);

        /// <summary>
        /// 当前公司代码
        /// </summary>
        public string Company => _company;

        /// <summary>
        /// 当前工厂(Plant)。空串 = 不限定工厂。
        /// </summary>
        public string Plant => _plant;

        /// <summary>
        /// 最近一次 Epicor API 调用返回的错误信息（如果有）。格式示例：
        /// "HttpStatus:404, Error:方法不存在, CorrelationId:xxx"
        /// 存储于 HttpContext.Items 中的线程安全队列（每请求隔离）：并发查询分支各自追加错误，
        /// 不会出现 Dictionary 并发写损坏，也不会因"后写覆盖"随机丢失错误信息。
        /// 无 HttpContext 时回退到实例级队列。
        /// </summary>
        public string LastApiError
        {
            get => ApiErrorQueue.TryPeek(out var error) ? error : string.Empty;
            private set
            {
                var queue = ApiErrorQueue;
                while (queue.TryDequeue(out _)) { }
                if (!string.IsNullOrEmpty(value)) queue.Enqueue(value);
            }
        }

        private const string ApiErrorItemKey = "Epicor.LastApiErrors";
        private readonly ConcurrentQueue<string> _fallbackApiErrors = new();
        private readonly IHttpContextAccessor? _httpContextAccessor;

        /// <summary>
        /// 当前请求级的错误队列。HttpContext.Items 是非线程安全的 Dictionary，
        /// 因此读与写都必须在同一把锁内，只把线程安全的 ConcurrentQueue 实例放入其中。
        /// （仅在锁内写、锁外读仍可能与写入并发，导致字典内部状态损坏。）
        /// </summary>
        private ConcurrentQueue<string> ApiErrorQueue
        {
            get
            {
                var context = _httpContextAccessor?.HttpContext;
                if (context == null) return _fallbackApiErrors;

                var items = context.Items;
                lock (items)
                {
                    if (items.TryGetValue(ApiErrorItemKey, out var created) && created is ConcurrentQueue<string> createdQueue)
                        return createdQueue;

                    var newQueue = new ConcurrentQueue<string>();
                    items[ApiErrorItemKey] = newQueue;
                    return newQueue;
                }
            }
        }

        /// <summary>清除最近一次 API 错误状态（正常业务流程开始前调用）。</summary>
        public void ClearApiError() => LastApiError = string.Empty;

        /// <summary>设置 API 错误信息（供上层业务代码在检测到业务错误时设置）。</summary>
        public void SetApiError(string error) => LastApiError = error ?? string.Empty;

        /// <summary>
        /// 初始化 Epicor 服务
        /// 使用 IHttpClientFactory 管理连接池，LicenseId 等配置从 EpicorSettings 读取
        /// </summary>
        public EpicorAuthService(string userID, string password, string apiUrl, string apiKey,
            IHttpClientFactory httpClientFactory, IOptions<EpicorSettings> settings,
            string company, string plant, ILogger<EpicorAuthService>? logger = null,
            IHttpContextAccessor? httpContextAccessor = null)
        {
            _userId = userID ?? throw new ArgumentNullException(nameof(userID));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _tokenUrl = (apiUrl ?? throw new ArgumentNullException(nameof(apiUrl))).TrimEnd('/');
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
            // company/plant 必填（无默认值）：多 Plant 隔离的根基，漏传必须在编译期/构造期暴露，
            // 任何默认值都会让"漏传"静默落到错误公司（CallSettings 与 OData URL 双路生效，极难定位）
            _company = company ?? throw new ArgumentNullException(nameof(company));
            _plant = plant ?? string.Empty;
            _logger = logger ?? NullLogger<EpicorAuthService>.Instance;
            _httpContextAccessor = httpContextAccessor;

            // 从配置读取（无配置时使用默认值）
            var epicorSettings = settings?.Value ?? new EpicorSettings();
            _licenseId = epicorSettings.LicenseId;
            _timeoutSeconds = epicorSettings.DefaultTimeoutSeconds > 0 ? epicorSettings.DefaultTimeoutSeconds : 30;
            _maxRetries = epicorSettings.MaxRetries >= 0 ? epicorSettings.MaxRetries : 1;
            _tokenExpiryMinutes = epicorSettings.TokenExpiryMinutes > 0 ? epicorSettings.TokenExpiryMinutes : 480;
            _tokenRefreshAheadMinutes = epicorSettings.TokenRefreshAheadMinutes >= 0 ? epicorSettings.TokenRefreshAheadMinutes : 5;

            // 预缓存 Header JSON（每次请求都用到，内容不变）；
            // Plant 为空串时按空串序列化，表示不限定工厂
            _callSettingsJson = JsonConvert.SerializeObject(new { Company = _company, Plant = _plant }, _jsonSettings);
            _licenseJson = JsonConvert.SerializeObject(new { ClaimedLicense = _licenseId }, _jsonSettings);

            // 提取基础 URL（仅截断末尾的 /TokenResource.svc；
            // 不用 Replace：避免把路径中间出现的同名片段也一并替换掉）
            const string tokenSuffix = "/TokenResource.svc";
            _baseUrl = _tokenUrl.EndsWith(tokenSuffix, StringComparison.OrdinalIgnoreCase)
                ? _tokenUrl.Substring(0, _tokenUrl.Length - tokenSuffix.Length)
                : _tokenUrl;
            _baseUrl = _baseUrl.TrimEnd('/');

            // 使用 IHttpClientFactory 注入，客户端按需短生命周期创建（底层连接池由工厂管理）
            if (httpClientFactory == null)
                throw new ArgumentNullException(nameof(httpClientFactory));
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// 创建短生命周期 BO HttpClient。
        /// 按方法选择命名客户端：GET 只读 → 允许连接重置重试；POST 可能写 → 连接重置不重试（防重复过账）。
        /// </summary>
        private HttpClient CreateBoClient(HttpMethod method)
        {
            var client = _httpClientFactory.CreateClient(ResolveClientName(method));
            client.BaseAddress = new Uri(_baseUrl + "/api/v2/");
            client.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);
            return client;
        }

        /// <summary>
        /// 创建短生命周期 OData HttpClient。
        /// 按方法选择命名客户端（BAQ 查询为 POST 但只读，同样走写客户端保守策略，避免任何重复投递）。
        /// </summary>
        private HttpClient CreateODataClient(HttpMethod method)
        {
            var client = _httpClientFactory.CreateClient(ResolveClientName(method));
            client.BaseAddress = new Uri(_baseUrl + $"/api/v2/odata/{_company}/");
            client.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);
            return client;
        }

        /// <summary>
        /// 创建短生命周期 Token HttpClient。
        /// Token 获取虽为 POST，但重复获取不产生业务副作用，使用可安全重试的幂等客户端。
        /// </summary>
        private HttpClient CreateTokenClient()
        {
            var client = _httpClientFactory.CreateClient(IdempotentClientName);
            client.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);
            return client;
        }

        /// <summary>
        /// 按 HTTP 方法选择命名客户端：
        /// 幂等方法走 "EpicorApiIdempotent"（连接层异常可全部重试），
        /// 其余（POST/PATCH，可能已产生业务副作用）走 "EpicorApi"（连接重置不重试）。
        /// </summary>
        private static string ResolveClientName(HttpMethod method)
            => IsIdempotentMethod(method) ? IdempotentClientName : WriteClientName;

        private static bool IsIdempotentMethod(HttpMethod? method)
            => method == HttpMethod.Get || method == HttpMethod.Head
            || method == HttpMethod.Options || method == HttpMethod.Put
            || method == HttpMethod.Delete || method == HttpMethod.Trace;

        /// <summary>
        /// 向 BAQ 执行参数列表追加 Plant 条件（仅在配置了 Plant 时）。
        /// _plant 为空（东莞）时直接返回，请求不追加 Plant 参数。
        /// 多站点隔离依赖：BAQ 列表类查询的数据可见性需由 Plant 参数收敛到当前工厂。
        /// 参数名与 Epicor 侧 BAQ 定义的 @Plant 参数一致。
        /// </summary>
        private void AddPlantParameter(List<object> parameters)
        {
            if (string.IsNullOrEmpty(_plant)) return;
            parameters.Add(new { ParameterID = "Plant", ParameterValue = _plant, ValueType = "string", IsEmpty = false });
        }

        #region 凭证一致性检查（供工厂判断是否需要重建实例）
        /// <summary>
        /// 检查当前实例的凭证是否与传入凭证匹配。
        /// 用于工厂判断：当用户以不同密码/API Key重试登录时，应重建实例而非复用旧的（含错误凭证）。
        /// </summary>
        public bool HasCredentials(string userId, string password, string apiKey)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(apiKey))
                return false;

            return string.Equals(_userId, userId, StringComparison.Ordinal)
                && string.Equals(_password, password, StringComparison.Ordinal)
                && string.Equals(_apiKey, apiKey, StringComparison.Ordinal);
        }
        #endregion
        // ==================== 释放资源 ====================

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            // 注：HttpClient 实例由 IHttpClientFactory 管理生命周期，无需手动 Dispose。
            // _tokenLock（SemaphoreSlim）不手动 Dispose：同一实例会被多个并发请求共享，
            // 工厂在缓存淘汰/登出路径调用 Dispose 会与在途请求的
            // WaitAsync / Release 产生竞态，触发 ObjectDisposedException。
            // SemaphoreSlim 未访问 AvailableWaitHandle，不占非托管资源，交由 GC 回收即可。
        }

    }
}