using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WarehouseScanner.Services;
using WarehouseScannerCore.Utilities;

namespace WarehouseScannerCore.Services
{
    #region 查询网关接口
    /// <summary>
    /// Epicor 查询网关接口
    /// 设计目标：
    /// 1. 隔离 Controller 与底层 EpicorAuthService，便于单元测试
    /// 2. 所有方法返回投影 DTO，避免将 Epicor 内部类型泄露到业务层
    /// 3. 使用工厂模式动态创建 EpicorAuthService（依赖会话中的凭证）
    /// </summary>
    public interface IEpicorQueryGateway
    {
        /// <summary>指示最近一次 Epicor API 调用是否返回错误（非 HTTP 200 或包含 ErrorMessage）</summary>
        bool HasApiError { get; }

        /// <summary>最近一次 Epicor API 调用返回的错误信息（可直接显示给用户）</summary>
        string LastApiError { get; }

        /// <summary>验证工单状态是否允许操作</summary>
        Task<JobStatusInfo> ValidateJobStatusAsync(string workOrderNo);

        /// <summary>查询工单物料需求和已发情况</summary>
        Task<List<JobMaterialInfo>> QueryJobMtlIssued(string workOrderNo, int assemblySeq, string materialCode);

        /// <summary>查询指定库位/批次下的物料库存</summary>
        Task<List<PartInventoryInfo>> GetPartNumInventory(
            string materialCode, string whseCode, string binNum, string lotNumber);

        /// <summary>查询物料简易库存信息（汇总可用量）</summary>
        Task<List<PartInventoryInfo>> GetSimplePartBinInventory(string materialCode);

        /// <summary>
        /// 调用 Epicor BAQ Inve_view 查询明细库存（可按 物料/仓库/库位/批次 过滤，未传的维度不参与过滤）。
        /// 解析字段使用 Epicor BAQ 返回的 PartBin_* 前缀：尤其 [PartBin].[LotNum]（LotNum）。
        /// </summary>
        Task<List<PartInventoryInfo>> GetInventoryDetailByInveViewBAQAsync(
            string partNum, string warehouseCode, string? binNum = null, string? lotNumber = null);
    }
    #endregion

    #region 投影 DTO 定义
    /// <summary>
    /// 工单状态投影 DTO
    /// </summary>
    public class JobStatusInfo
    {
        public string JobNum { get; set; } = string.Empty;
        public string PartNum { get; set; } = string.Empty;
        public bool? JobReleased { get; set; }
        public bool? JobComplete { get; set; }
        public bool? JobClosed { get; set; }
        public bool IsValid => JobClosed != true && JobComplete != true && JobReleased == true;
    }

    /// <summary>
    /// 工单物料投影 DTO
    /// </summary>
    public class JobMaterialInfo
    {
        public string JobNum { get; set; } = string.Empty;
        public int AssemblySeq { get; set; }
        public int MtlSeq { get; set; }
        public string PartNum { get; set; } = string.Empty;
        public string MaterialCode => PartNum;
        public string DspIUM { get; set; } = string.Empty;
        public decimal RequiredQty { get; set; }
        public decimal QtyPreviouslyIssued { get; set; }
        public decimal RemainingQty => RequiredQty - QtyPreviouslyIssued;
    }

    /// <summary>
    /// 物料库存投影 DTO
    /// </summary>
    public class PartInventoryInfo
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

    #region 查询网关实现
    /// <summary>
    /// Epicor 查询网关实现
    /// 使用工厂模式从 HttpContext.Session 动态创建 EpicorAuthService
    /// </summary>
    public class EpicorQueryGateway : IEpicorQueryGateway
    {
        private readonly IEpicorAuthServiceFactory _authServiceFactory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IDataProtector _protector;
        private readonly ILogger<EpicorQueryGateway> _logger;

        /// <summary>最近一次 Epicor API 调用是否返回错误（由底层 AuthService 同步）</summary>
        public bool HasApiError => !string.IsNullOrEmpty(LastApiError);

        /// <summary>
        /// 最近一次 Epicor API 调用返回的错误信息（由底层 AuthService 同步）。
        /// 存储于 HttpContext.Items 中的线程安全队列（每请求隔离）：并发查询分支各自追加错误，
        /// 不会因字典并发写而损坏，也不会因"后写覆盖"随机丢失错误信息。
        /// </summary>
        public string LastApiError
        {
            get => ErrorQueue.TryPeek(out var error) ? error : string.Empty;
            private set
            {
                var queue = ErrorQueue;
                while (queue.TryDequeue(out _)) { }
                if (!string.IsNullOrEmpty(value)) queue.Enqueue(value);
            }
        }

        private const string ApiErrorItemKey = "Epicor.GatewayApiErrors";
        private readonly ConcurrentQueue<string> _fallbackApiErrors = new();

        /// <summary>
        /// 当前请求级的网关错误队列。HttpContext.Items 是非线程安全的 Dictionary，
        /// 因此读与写都必须在同一把锁内，只把线程安全的 ConcurrentQueue 实例放入其中。
        /// （仅在锁内写、锁外读仍可能与写入并发，导致字典内部状态损坏。）
        /// </summary>
        private ConcurrentQueue<string> ErrorQueue
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

        public EpicorQueryGateway(
            IEpicorAuthServiceFactory authServiceFactory,
            IHttpContextAccessor httpContextAccessor,
            IDataProtectionProvider protectionProvider,
            ILogger<EpicorQueryGateway> logger)
        {
            _authServiceFactory = authServiceFactory ?? throw new ArgumentNullException(nameof(authServiceFactory));
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            _protector = (protectionProvider ?? throw new ArgumentNullException(nameof(protectionProvider)))
                .CreateProtector("AccountController.SensitiveData");
            _logger = logger ?? NullLogger<EpicorQueryGateway>.Instance;
        }

        #region 会话辅助方法：从 Session 创建 EpicorAuthService
        /// <summary>
        /// 从当前 HttpContext 会话创建 EpicorAuthService（复用统一工厂路径）
        /// </summary>
        private EpicorAuthService GetAuthService()
        {
            var session = _httpContextAccessor.HttpContext?.Session
                ?? throw new UnauthorizedAccessException("会话不可用，请重新登录");
            // 每次调用前清空网关自身的错误状态：HasApiError 表达的是"最近一次调用"的结果，
            // 不清空会让上一次的失败结果粘住，掩盖本次调用的真实结果（业务层已按此语义编写校验）。
            LastApiError = string.Empty;
            return _authServiceFactory.CreateFromSession(session, _protector);
        }

        /// <summary>
        /// 同步底层 EpicorAuthService 的 API 错误状态到当前 Gateway。
        /// 每次调用底层业务方法后调用，确保上游可通过 HasApiError / LastApiError 检查。
        /// </summary>
        private void SyncApiErrorState(EpicorAuthService authService)
        {
            LastApiError = authService?.LastApiError ?? string.Empty;
        }
        #endregion

        #region 工单状态验证
        /// <summary>
        /// 验证工单状态是否允许发料
        /// </summary>
        public async Task<JobStatusInfo> ValidateJobStatusAsync(string workOrderNo)
        {
            if (string.IsNullOrWhiteSpace(workOrderNo))
                return new JobStatusInfo();

            var authService = GetAuthService();
            var raw = await authService.ValidateJobStatusAsync(workOrderNo);
            SyncApiErrorState(authService); // 同步底层 API 错误状态



            return new JobStatusInfo
            {
                JobNum = raw.JobNum ?? workOrderNo,
                PartNum = raw.PartNum ?? string.Empty,
                JobReleased = raw.JobReleased,
                JobComplete = raw.JobComplete,
                JobClosed = raw.JobClosed
            };
        }
        #endregion

        #region 工单物料查询
        /// <summary>
        /// 查询工单物料需求
        /// </summary>
        public async Task<List<JobMaterialInfo>> QueryJobMtlIssued(
            string workOrderNo, int assemblySeq, string materialCode)
        {
            var authService = GetAuthService();
            var rawList = await authService.QueryJobMtlIssued(
                workOrderNo, assemblySeq, materialCode ?? string.Empty);
            SyncApiErrorState(authService); // 同步底层 API 错误状态

            if (rawList == null || rawList.Count == 0)
            {
                _logger.LogInformation(
                    "无工单物料数据: JobNum={JobNum}, AssemblySeq={AssemblySeq}, MaterialCode={MaterialCode}",
                    workOrderNo, assemblySeq, materialCode);
                return new List<JobMaterialInfo>();
            }

            return rawList
                .Select(raw => new JobMaterialInfo
                {
                    JobNum = workOrderNo,
                    AssemblySeq = assemblySeq,
                    MtlSeq = raw.MtlSeq,
                    PartNum = raw.PartNum ?? string.Empty,
                    DspIUM = raw.DspIUM ?? string.Empty,
                    RequiredQty = raw.RequiredQty,
                    QtyPreviouslyIssued = raw.QtyPreviouslyIssued
                })
                .ToList();
        }
        #endregion

        #region 物料库存查询
        /// <summary>
        /// 查询指定条件下的物料库存
        /// </summary>
        public async Task<List<PartInventoryInfo>> GetPartNumInventory(
            string materialCode, string whseCode, string binNum, string lotNumber)
        {
            var authService = GetAuthService();
            // 自动翻页取全部匹配行后再聚合，避免单页截断导致库存少算
            var rawList = await authService.GetPartNumInventoryAll(
                materialCode, whseCode, binNum, lotNumber);
            SyncApiErrorState(authService); // 同步底层 API 错误状态

            if (rawList == null || rawList.Count == 0)
                return new List<PartInventoryInfo>();

            return rawList
                .Select(raw => new PartInventoryInfo
                {
                    PartNum = raw.PartNum ?? materialCode,
                    PartDesc = raw.PartDesc ?? string.Empty,
                    Plant = raw.Plant ?? string.Empty,
                    WhseCode = raw.WhseCode ?? string.Empty,
                    BinNum = raw.BinNum ?? string.Empty,
                    LotNumber = raw.LotNumber ?? string.Empty,
                    QtyOnHand = raw.QtyOnHand,
                    QtyAllocated = raw.QtyAllocated,
                    IUM = raw.IUM ?? string.Empty
                })
                .ToList();
        }
        #endregion

        #region 简易库存查询（汇总）
        /// <summary>
        /// 查询物料全量库存
        /// </summary>
        public async Task<List<PartInventoryInfo>> GetSimplePartBinInventory(string materialCode)
        {
            var authService = GetAuthService();
            var rawList = await authService.GetSimplePartBinInventory(materialCode);
            SyncApiErrorState(authService); // 同步底层 API 错误状态

            if (rawList == null || rawList.Count == 0)
                return new List<PartInventoryInfo>();

            return rawList
                .Select(raw => new PartInventoryInfo
                {
                    PartNum = raw.PartNum ?? materialCode,
                    PartDesc = raw.PartDesc ?? string.Empty,
                    Plant = raw.Plant ?? string.Empty,
                    WhseCode = raw.WhseCode ?? string.Empty,
                    BinNum = raw.BinNum ?? string.Empty,
                    LotNumber = raw.LotNumber ?? string.Empty,
                    QtyOnHand = raw.QtyOnHand,
                    QtyAllocated = raw.QtyAllocated,
                    IUM = raw.IUM ?? string.Empty
                })
                .ToList();
        }
        #endregion

        #region Inve_view BAQ 明细库存查询
        /// <summary>
        /// 调用 Epicor BAQ Inve_view 查询明细库存，解析 PartBin_LotNum（[PartBin].[LotNum]）。
        /// 按 物料/仓库/库位/批次 过滤（空值维度不参与过滤）；QtyOnHand 为在手库存。
        /// </summary>
        public async Task<List<PartInventoryInfo>> GetInventoryDetailByInveViewBAQAsync(
            string partNum, string warehouseCode, string? binNum = null, string? lotNumber = null)
        {
            var authService = GetAuthService();
            // 复用 BAQ Inve_view（参数格式与已有 QueryInveViewByBAQAsync 一致）
            var rawList = await authService.ListInveViewInventoryByBAQAsync(partNum, warehouseCode, binNum, lotNumber);
            SyncApiErrorState(authService);

            if (rawList == null || rawList.Count == 0)
                return new List<PartInventoryInfo>();

            return rawList
                .Select(raw => new PartInventoryInfo
                {
                    PartNum = raw.PartNum ?? string.Empty,
                    PartDesc = raw.PartDescription ?? string.Empty,
                    WhseCode = raw.WarehouseCode ?? string.Empty,
                    BinNum = raw.BinNum ?? string.Empty,
                    // 按用户要求：批号来自 Epicor [PartBin].[LotNum] 原生列（PartBin_LotNum）
                    LotNumber = raw.LotNum ?? string.Empty,
                    QtyOnHand = raw.OnhandQty,
                    // BAQ Inve_view 不返回已分配数量，故恒为 0；
                    // 因此本查询路径下 PartInventoryInfo.AvailableQty == QtyOnHand（"库存数"口径即"在手数量"）。
                    QtyAllocated = 0,
                    IUM = raw.IUM ?? string.Empty
                })
                .ToList();
        }
        #endregion
    }
    #endregion
}
