namespace WarehouseScannerCore.Models
{
    /// <summary>
    /// Epicor 调用上下文：站点 / 公司 / 工厂。
    /// 用于替代散落的 (company, plant) 位置参数，防止站点代码被误当公司传入。
    /// </summary>
    public sealed record EpicorSiteTarget(string SiteCode, string Company, string Plant);
}
