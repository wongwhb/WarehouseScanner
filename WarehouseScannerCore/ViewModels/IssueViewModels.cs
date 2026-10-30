using System.Collections.Generic;

namespace WarehouseScannerCore.ViewModels
{
    #region 发料首页 ViewModel
    /// <summary>
    /// 发料首页视图模型
    /// 替代 ViewBag/ViewData，提供编译期类型检查
    /// 包含用户信息、本地化文本、操作上下文等页面渲染所需全部数据
    /// </summary>
    public class IssueIndexViewModel
    {
        #region 基本用户信息
        /// <summary>当前登录用户 ID</summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>用户角色（用于权限展示控制）</summary>
        public string UserRole { get; set; } = string.Empty;

        /// <summary>当前工厂/站点名称</summary>
        public string SiteName { get; set; } = string.Empty;

        /// <summary>登录时间（字符串展示）</summary>
        public string LoginTime { get; set; } = string.Empty;
        #endregion

        #region 本地化
        /// <summary>当前语言代码，如 zh-CN / en-US</summary>
        public string CurrentCulture { get; set; } = "zh-CN";

        /// <summary>
        /// 本地化文本字典：section.key → 文本内容
        /// 视图中通过 @Model.GetText("MaterialIssue", "Title") 读取
        /// </summary>
        public Dictionary<string, string> Text { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 从 Text 字典按 section.key 获取本地化字符串
        /// </summary>
        public string GetText(string section, string key)
        {
            if (Text == null) return $"[{section}.{key}]";
            return Text.TryGetValue($"{section}.{key}", out var value)
                ? value
                : $"[{section}.{key}]";
        }
        #endregion

        #region 操作上下文
        /// <summary>是否具备发料权限</summary>
        public bool HasIssuePermission { get; set; } = true;

        /// <summary>页面副标题（由上层设置）</summary>
        public string PageTitle => GetText("MaterialIssue", "Title");
        #endregion
    }
    #endregion

    #region 库存信息展示 ViewModel
    /// <summary>
    /// 库存信息展示视图模型（详情/列表展示用）
    /// 与 Epicor 返回的 PartBinSearchResult 解耦，避免视图直接依赖数据层类型
    /// </summary>
    public class PartInventoryViewModel
    {
        public string PartNum { get; set; } = string.Empty;
        public string PartDesc { get; set; } = string.Empty;
        public string Plant { get; set; } = string.Empty;
        public string WhseCode { get; set; } = string.Empty;
        public string BinNum { get; set; } = string.Empty;
        public string LotNumber { get; set; } = string.Empty;
        public decimal QtyOnHand { get; set; }
        public decimal QtyAllocated { get; set; }
        public decimal AvailableQty => QtyOnHand - QtyAllocated;
        public string IUM { get; set; } = string.Empty;
    }
    #endregion

    #region 工单信息展示 ViewModel
    /// <summary>
    /// 工单状态视图模型
    /// </summary>
    public class JobStatusViewModel
    {
        public string JobNum { get; set; } = string.Empty;
        public string PartNum { get; set; } = string.Empty;
        public bool JobReleased { get; set; }
        public bool JobComplete { get; set; }
        public bool JobClosed { get; set; }
        public string StatusText => JobClosed ? "已关闭"
                                       : JobComplete ? "已完工"
                                       : JobReleased ? "已发放" : "未发放";
    }
    #endregion
}
