using Newtonsoft.Json;
using System;

namespace WarehouseScannerCore.Models
{
    #region API 响应状态码枚举
    /// <summary>
    /// API 响应状态码枚举
    /// 与 int 隐式兼容，可直接赋值给 int 类型的 Code 属性
    /// </summary>
    public enum ApiCode
    {
        Success = 0,
        Error = 500,
        BadRequest = 400,
        Unauthorized = 401,
        Forbidden = 403,
        NotFound = 404
    }
    #endregion

    #region 统一 API 响应接口
    /// <summary>
    /// API 响应通用接口 - 用于过滤器识别
    /// </summary>
    public interface IApiResponse
    {
        bool Success { get; }
        string Message { get; }
        ApiCode Code { get; }
    }
    #endregion

    #region 泛型 API 响应类
    /// <summary>
    /// API 统一响应格式（泛型）
    /// 设计目标：所有 AJAX Action 统一返回此格式，前端处理逻辑一致
    /// </summary>
    /// <typeparam name="T">数据负载类型</typeparam>
    [Serializable]
    public class ApiResponse<T> : IApiResponse
    {
        /// <summary>是否成功</summary>
        [JsonProperty("success")]
        public bool Success { get; set; }

        /// <summary>状态码：ApiCode.Success=成功，其他=错误</summary>
        [JsonProperty("code")]
        public ApiCode Code { get; set; }

        /// <summary>提示消息</summary>
        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>业务数据</summary>
        [JsonProperty("data")]
        public T? Data { get; set; }

        #region 工厂方法
        /// <summary>创建成功响应</summary>
        public static ApiResponse<T> Ok(T data, string message = "操作成功")
        {
            return new ApiResponse<T>
            {
                Success = true,
                Code = ApiCode.Success,
                Message = message,
                Data = data
            };
        }

        /// <summary>创建失败响应</summary>
        public static ApiResponse<T> Fail(string message, ApiCode code = ApiCode.Error)
        {
            return new ApiResponse<T>
            {
                Success = false,
                Code = code,
                Message = message,
                Data = default
            };
        }
        #endregion
    }
    #endregion

    #region 非泛型便捷类
    /// <summary>
    /// API 统一响应格式（非泛型便捷类）
    /// 适用于不需要数据负载或使用匿名对象的场景
    /// </summary>
    public class ApiResponse : ApiResponse<object>
    {
        public static new ApiResponse Ok(object data, string message = "操作成功")
            => new() { Success = true, Code = ApiCode.Success, Message = message, Data = data };

        public static new ApiResponse Fail(string message, ApiCode code = ApiCode.Error)
            => new() { Success = false, Code = code, Message = message, Data = null };
    }
    #endregion
}
