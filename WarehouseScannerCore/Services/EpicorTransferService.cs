using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace WarehouseScanner.Services
{
    /// <summary>
    /// Epicor 库存转移服务类
    /// 通过 InventoryQtyAdjSvc 实现库存转移（源位置减少 + 目标位置增加）
    /// </summary>
    public partial class EpicorTransferService
    {
        private readonly EpicorAuthService _authService;
        private readonly ILogger<EpicorTransferService>? _logger;

        public EpicorTransferService(EpicorAuthService authService, string company, ILogger<EpicorTransferService>? logger = null)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _logger = logger;
        }

        #region 公共方法

        /// <summary>
        /// 执行库存转移
        /// </summary>
        /// <param name="partNum">物料代码</param>
        /// <param name="quantity">转移数量</param>
        /// <param name="fromWarehouse">源仓库</param>
        /// <param name="fromBinNum">源库位</param>
        /// <param name="fromLotNum">源批次</param>
        /// <param name="toWarehouse">目标仓库</param>
        /// <param name="toBinNum">目标库位</param>
        /// <param name="toLotNum">目标批次</param>
        /// <param name="isToBatchNo">目标批号是否存在</param>
        ///  /// <param name="remark">备注（可传入调整原因）</param>
        /// <returns>最终保存后的响应 JObject（最后一次调整的结果）</returns>
        public async Task<JObject> TransferInventoryAsync(
            string partNum,
            decimal quantity,
            string fromWarehouse,
            string fromBinNum,
            string fromLotNum,
            string toWarehouse,
            string toBinNum,
            string toLotNum,
            bool isToBatchNo,
            string? remark = null)
        {
            if (quantity <= 0)
                throw new ArgumentException("转移数量必须大于0", nameof(quantity));

            JObject fromResult = null!;
            try
            {
                fromResult = await AdjustInventoryAsync(partNum, quantity, fromWarehouse, fromBinNum, fromLotNum, toWarehouse, toBinNum, toLotNum, isToBatchNo, remark);
                return fromResult;
            }
            catch (Exception ex)
            {
                // 说明：整个转移在单个 InvTransferSvc 单据内完成，最终由 CommitTransferAndUpdateHistory
                // 一次性事务提交。提交前的任一步骤失败均未改动库存；提交本身失败则 Epicor 事务整体回滚，
                // 源与目标原子地一起回滚。故此处无需（也不应）手动反向调整源位置，仅记录日志并透传异常。
                _logger?.LogError(ex, "库存转移失败（Epicor 事务已保证整体回滚）。物料 {PartNum}, 数量 {Quantity}", partNum, quantity);
                throw;
            }
        }

        #endregion
    }
}