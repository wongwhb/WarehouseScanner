using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WarehouseScannerCore.Utilities
{
    /// <summary>
    /// Epicor JSON 响应处理共享工具
    /// 统一 ExtractDs / 序列化设置 / 响应解析，消除跨服务重复代码
    /// 支持三种错误格式：
    /// 1. Epicor BO 标准格式 {HttpStatus, ErrorMessage, ErrorType, CorrelationId}
    /// 2. RFC 9110 标准格式 {type, title, status, traceId}
    /// 3. 嵌套 returnObj 格式
    /// </summary>
    public static class EpicorJsonHelper
    {
        /// <summary>全局共享的 JSON 序列化设置（忽略 null 值）</summary>
        public static readonly JsonSerializerSettings IgnoreNullSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// 从 Epicor 服务响应中提取 ds 对象。
        /// 按优先级依次尝试：
        ///   1. returnObj.parameters.ds（Epicor BO 标准信封）
        ///   2. returnObj.ds
        ///   3. returnObj 下所有 JArray 属性聚合
        ///   4. 顶层 parameters.ds
        ///   5. 顶层 ds
        ///   6. 顶层所有 JArray 属性聚合
        ///   7. 兜底返回原始响应
        /// </summary>
        public static JObject ExtractDs(JObject? response)
        {
            if (response == null) return null!;

            // 格式 3: 嵌套信封 {"returnObj": {...}}
            if (response["returnObj"] is JObject returnObj)
            {
                if (returnObj["parameters"]?["ds"] is JObject dsFromReturnParams)
                    return dsFromReturnParams;

                if (returnObj["ds"] is JObject dsFromReturn)
                    return dsFromReturn;

                var returnArrays = CollectArrays(returnObj);
                if (returnArrays.HasValues) return returnArrays;
            }

            // 格式 1: {"parameters": {"ds": {...}}}
            if (response["parameters"]?["ds"] is JObject dsFromParams)
                return dsFromParams;

            // 格式 2: 直接 {"ds": {...}}
            if (response["ds"] is JObject directDs)
                return directDs;

            // 兜底 1: 收集响应根下所有 JArray 属性
            var arrays = CollectArrays(response);
            if (arrays.HasValues) return arrays;

            return response;
        }

        /// <summary>收集对象属性中所有 JArray 类型的值，组合为新的 JObject</summary>
        private static JObject CollectArrays(JObject source)
        {
            var result = new JObject();
            foreach (var prop in source.Properties())
            {
                if (prop.Value is JArray)
                    result[prop.Name] = prop.Value;
            }
            return result;
        }

        /// <summary>序列化对象为 JSON（忽略 null，无缩进）</summary>
        public static string Serialize(object obj)
            => JsonConvert.SerializeObject(obj, Formatting.None, IgnoreNullSettings);

        #region 错误检测与解析

        /// <summary>
        /// 尝试读取 RFC 9110 的 status 字段。非数字或缺失时返回 false，不抛异常。
        /// 统一 HasError 与 TryParseResponse 的解析行为，避免同一响应在两条路径上得出不同结论。
        /// </summary>
        private static bool TryReadStatus(JObject obj, out int status)
        {
            status = 0;
            var token = obj["status"];
            if (token == null || token.Type == JTokenType.Null) return false;

            try
            {
                status = token.ToObject<int>();
                return true;
            }
            catch (Exception)
            {
                // 非数字 status（如 "error"），无法按 RFC 9110 判定，交由后续格式检测处理
                return false;
            }
        }

        /// <summary>
        /// 检测响应是否包含 HTTP 错误状态（不抛异常）
        /// 支持：Epicor BO 格式 (HttpStatus!=200)、RFC 9110 格式 (status!=200)、ErrorMessage
        /// </summary>
        public static bool HasError(JObject response)
        {
            if (response == null) return true;

            // 格式 1: Epicor BO 标准格式 {HttpStatus, ErrorMessage}
            if (response.ContainsKey("HttpStatus"))
            {
                var httpStatus = response["HttpStatus"]?.ToString();
                if (httpStatus != "200" && httpStatus != "OK" && !string.IsNullOrEmpty(httpStatus))
                    return true;
            }

            // 格式 2: RFC 9110 标准格式 {type, title, status, traceId}
            if (TryReadStatus(response, out int status) && status != 200 && status != 0)
                return true;

            // 格式 3: 只有 ErrorMessage（且非空）
            if (response["ErrorMessage"] != null && !string.IsNullOrWhiteSpace(response["ErrorMessage"]!.ToString()))
                return true;

            return false;
        }

        /// <summary>
        /// 从响应中提取人类可读的错误信息（不抛异常）
        /// </summary>
        public static string GetErrorMessage(JObject response)
        {
            if (response == null) return "响应数据为空";

            // RFC 9110 格式优先
            if (response["title"] != null)
            {
                string title = response["title"]!.ToString();
                string status = response["status"]?.ToString() ?? "";
                string traceId = response["traceId"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(title))
                    return $"[{status}] {title} (TraceId: {traceId})";
            }

            // Epicor BO 标准格式
            string? errorMsg = response["ErrorMessage"]?.ToString()
                              ?? response["Error"]?.ToString()
                              ?? response["ReasonPhrase"]?.ToString();

            if (!string.IsNullOrWhiteSpace(errorMsg))
                return errorMsg;

            // 回退：提取 HttpStatus
            if (response["HttpStatus"] != null)
                return $"API 返回错误状态 (HttpStatus: {response["HttpStatus"]})";

            return "未知错误";
        }

        /// <summary>
        /// 安全解析响应 JSON - 不抛异常，返回 (success, errorMessage)
        /// 同时检测三种错误格式（Epicor BO / RFC 9110 / ErrorMessage）
        /// </summary>
        public static (JObject response, bool hasError, string errorMessage) TryParseResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
                return (null!, true, "响应数据为空");

            try
            {
                var obj = JObject.Parse(responseJson);

                // 检测 RFC 9110 格式（如 TokenResource 返回的 401）
                if (TryReadStatus(obj, out int status) && status != 200 && status != 0)
                {
                    string title = obj["title"]?.ToString() ?? "API 调用失败";
                    string traceId = obj["traceId"]?.ToString() ?? "";
                    return (obj, true, $"[{status}] {title} (TraceId: {traceId})");
                }

                // 检测 Epicor BO 标准格式 {HttpStatus, ErrorMessage}
                if (obj["HttpStatus"] != null)
                {
                    string statusStr = obj["HttpStatus"]!.ToString();
                    if (statusStr != "200" && statusStr != "OK" && !string.IsNullOrEmpty(statusStr))
                    {
                        string msg = obj["ErrorMessage"]?.ToString()
                                    ?? obj["ReasonPhrase"]?.ToString()
                                    ?? "API 调用失败";
                        string correlationId = obj["CorrelationId"]?.ToString() ?? "";
                        return (obj, true, $"[{statusStr}] {msg} (CorrelationId: {correlationId})");
                    }
                }

                // 检测嵌套 returnObj 中的错误（某些 BO 方法的返回）
                if (obj["returnObj"] is JObject returnObj && returnObj["ErrorMessage"] != null)
                {
                    string err = returnObj["ErrorMessage"]!.ToString();
                    if (!string.IsNullOrWhiteSpace(err))
                        return (obj, true, err);
                }

                // 检测纯 ErrorMessage 字段
                if (obj["ErrorMessage"] != null && !string.IsNullOrWhiteSpace(obj["ErrorMessage"]!.ToString()))
                    return (obj, true, obj["ErrorMessage"]!.ToString());

                return (obj, false, string.Empty);
            }
            catch (Exception ex)
            {
                // 不暴露原始响应片段，避免泄露内部信息
                return (null!, true, $"响应解析失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 旧版兼容方法 - 解析响应 JSON，若包含错误则抛出异常
        /// 保留以向后兼容，新代码请使用 TryParseResponse
        /// </summary>
        public static JObject ParseResponse(string responseJson)
        {
            var (obj, hasError, errorMsg) = TryParseResponse(responseJson);
            if (hasError)
                throw new InvalidOperationException(errorMsg);
            return obj;
        }

        #endregion
    }
}
