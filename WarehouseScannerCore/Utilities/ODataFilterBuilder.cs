using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WarehouseScannerCore.Utilities
{
    /// <summary>
    /// OData $filter 构造工具类
    /// 职责：统一处理 OData 字符串字面量转义与 filter 构造，防止 OData 注入
    /// </summary>
    public static class ODataFilterBuilder
    {
        #region 输入白名单校验

        /// <summary>工单号格式校验：字母 + 数字 + 连字符</summary>
        private static readonly Regex JobNumPattern = new(@"^[A-Za-z0-9\-]+$", RegexOptions.Compiled);

        /// <summary>仓库/库位编码格式校验：字母 + 数字 + 连字符 + 下划线</summary>
        private static readonly Regex CodePattern = new(@"^[A-Za-z0-9\-_]+$", RegexOptions.Compiled);

        /// <summary>校验工单号格式是否合法</summary>
        public static bool IsValidJobNum(string value)
            => !string.IsNullOrWhiteSpace(value) && JobNumPattern.IsMatch(value);

        /// <summary>
        /// 校验物料编码格式是否合法。
        /// 单引号经 EscapeString 转义（' → ''）后本身安全，因此这里不拦截单引号；
        /// 非空即为合法（支持 105445_A(CN) 等任意格式），注入防护由 EscapeString 保证。
        /// </summary>
        public static bool IsValidPartNum(string value)
            => !string.IsNullOrWhiteSpace(value);

        /// <summary>校验仓库/库位编码格式是否合法</summary>
        public static bool IsValidCode(string value)
            => !string.IsNullOrWhiteSpace(value) && CodePattern.IsMatch(value);

        #endregion

        #region 字符串转义

        /// <summary>
        /// 转义 OData 字符串字面量
        /// 规则：单引号 → 两个单引号（OData 规范）
        /// </summary>
        public static string EscapeString(string value)
            => (value ?? string.Empty).Replace("'", "''");

        #endregion

        #region Filter 构造

        /// <summary>
        /// 构造单个相等条件：field eq 'value'
        /// </summary>
        public static string Eq(string field, string value)
            => $"{field} eq '{EscapeString(value)}'";

        /// <summary>
        /// 构造数字相等条件：field eq 123
        /// </summary>
        public static string Eq(string field, int value)
            => $"{field} eq {value}";

        /// <summary>
        /// 组合多个条件为 and 表达式
        /// </summary>
        public static string And(params string[] conditions)
            => string.Join(" and ", conditions.Where(c => !string.IsNullOrEmpty(c)));

        /// <summary>
        /// 生成完整的 OData URL（含 $filter 编码）
        /// </summary>
        /// <param name="entitySet">实体集名称，如 Erp.BO.WarehseSvc/Warehses</param>
        /// <param name="filter">已构造的 filter 表达式（未编码）</param>
        /// <param name="top">可选 $top 参数</param>
        public static string BuildUrl(string entitySet, string filter, int? top = null)
        {
            var url = $"{entitySet}?$filter={Uri.EscapeDataString(filter)}";
            if (top.HasValue)
                url += $"&$top={top.Value}";
            return url;
        }

        #endregion
    }
}
