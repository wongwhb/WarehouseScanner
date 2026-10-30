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
    /// <summary>嵌套模型类</summary>
    public partial class EpicorAuthService
    {
        // ==================== 模型类 ====================

        /// <summary>
        /// Token 响应类，匹配 Epicor 服务器返回的格式
        /// 服务器返回：{"AccessToken":"...","ExpiresIn":36000,"TokenType":"Bearer"}
        /// 如启用 refresh_token，还会包含："RefreshToken":"..."
        /// </summary>
        private class TokenResponse
        {
            [JsonProperty("AccessToken")]
            public string AccessToken { get; set; } = "";

            [JsonProperty("ExpiresIn")]
            public int ExpiresIn { get; set; }

            [JsonProperty("TokenType")]
            public string TokenType { get; set; } = "";

            [JsonProperty("RefreshToken")]
            public string RefreshToken { get; set; } = "";
        }

        // ... 其他模型类保持不变 ...
        public class PartBinSearchResult
        {
            public string Company { get; set; } = "";
            public string PartNum { get; set; } = "";
            public string Plant { get; set; } = "";
            public string WhseCode { get; set; } = "";
            public string BinNum { get; set; } = "";
            public string LotNumber { get; set; } = "";
            public string DimCode { get; set; } = "";
            public decimal QtyOnHand { get; set; }
            public string PartDesc { get; set; } = "";
            public string PlantName { get; set; } = "";
            public string WhseCodeDesc { get; set; } = "";
            public string BinDesc { get; set; } = "";
            public string LotNumberDesc { get; set; } = "";
            public string DimCodeDesc { get; set; } = "";
            public bool NonNettable { get; set; }
            public string IUM { get; set; } = "";
            public string BinType { get; set; } = "";
            public string ContractID { get; set; } = "";
            public string PCID { get; set; } = "";
            public bool SendToFSA { get; set; }
            public decimal QtyAllocated { get; set; }
            public int AttributeSetID { get; set; }
            public string AttributeSetDescription { get; set; } = "";
            public string AttributeSetShortDescription { get; set; } = "";
            public string RevisionNum { get; set; } = "";
            public string SysRowID { get; set; } = "";
            public string RowMod { get; set; } = "";
        }

        public class SimpleBinInventory
        {
            public string PartNum { get; set; } = "";
            public string PartDesc { get; set; } = "";
            public string Plant { get; set; } = "";
            public string WhseCode { get; set; } = "";
            public string WhseCodeDesc { get; set; } = "";
            public string BinNum { get; set; } = "";
            public string BinDesc { get; set; } = "";
            public string LotNumber { get; set; } = "";
            public decimal QtyOnHand { get; set; }
            public decimal QtyAllocated { get; set; }
            public decimal AvailableQty { get; set; }
            public string IUM { get; set; } = "";
            public override string ToString()
            {
                return $"{WhseCodeDesc}({WhseCode}) - {BinDesc}({BinNum}): {QtyOnHand}{IUM} (可用: {AvailableQty})";
            }
        }

        public class BAQ_Response
        {
            [JsonProperty("returnObj")]
            public BAQ_ReturnObj ReturnObj { get; set; } = new();
        }

        public class BAQ_ReturnObj
        {
            [JsonProperty("Results")]
            public List<BAQ_Result> Results { get; set; } = new();
        }

        public class BAQ_Result
        {
            [JsonProperty("EmpBasic_EmpID")]
            public string EmpBasic_EmpID { get; set; } = "";
        }

        public class BAQ_ResponseWTD
        {
            [JsonProperty("returnObj")]
            public BAQ_ReturnObjWTD ReturnObj { get; set; } = new();
        }

        public class BAQ_ReturnObjWTD
        {
            [JsonProperty("Results")]
            public List<BAQ_ResultWTD> Results { get; set; } = new();
        }

        public class BAQ_ResultWTD
        {
            [JsonProperty("EmpBasic_EmpID")]
            public string EmpBasic_EmpID { get; set; } = "";
            [JsonProperty("Calculated_PayHours")]
            public decimal Calculated_PayHours { get; set; }
            [JsonProperty("UD06_Key4")]
            public string UD06_Key4 { get; set; } = "";
            [JsonProperty("Calculated_LunchStart")]
            public string Calculated_LunchStart { get; set; } = null!;
        }

        public class JobMtlSearchResult
        {
            public string PartNum { get; set; } = "";
            public decimal RequiredQty { get; set; }
            public decimal QtyPreviouslyIssued { get; set; }
            public string DspIUM { get; set; } = "";
            public string BaseUOM { get; set; } = "";
            public int MtlSeq { get; set; }
            public string RelatedOperation { get; set; } = "";
            public Boolean IssuedComplete { get; set; } = false;
        }

        public class OrderRelInfo
        {
            public decimal OrderQty { get; set; }
            public decimal TotalJobStockShipped { get; set; }
            public decimal SellingRemainQty { get; set; }
            public string ShipLocked_c { get; set; } = "";
            public string ShipViaCode { get; set; } = "";
            public string PartNum { get; set; } = "";
            public string IUM { get; set; } = "";
            public string FirmRelease { get; set; } = "";
            public string Status { get; set; } = "";
            public int OrderRelNum { get; set; }
        }

        public class WhseBinInfo
        {
            public string WarehouseCode { get; set; } = "";
            public string BinNum { get; set; } = "";
            public string Description { get; set; } = "";
            public bool? InActive { get; set; }
        }

        public class LotInfo
        {
            public string Company { get; set; } = "";
            public string PartNum { get; set; } = "";
            public string LotNumber { get; set; } = "";
        }

        public class JobStatusValidateResult
        {
            public bool IsValid { get; set; } = false;
            public string JobNum { get; set; } = "";
            public string PartNum { get; set; } = "";
            public bool JobReleased { get; set; }
            public bool JobComplete { get; set; }
            public bool JobClosed { get; set; }
        }

        public class CustomerOrderShipmentResult
        {
            public string CustNum { get; set; } = "";
            public string CustName { get; set; } = "";
            public string CustomerReqDate { get; set; } = "";
            public string PO { get; set; } = "";
            public string Order { get; set; } = "";
            public string Line { get; set; } = "";
            public string Rel { get; set; } = "";
            public string Part { get; set; } = "";
            public string PartDescription { get; set; } = "";
            public decimal OurReqQty { get; set; }
            public decimal OnHandQty { get; set; }
            public string Comments { get; set; } = "";
        }

        public class InventoryModel
        {
            public string PartNum { get; set; } = "";
            public string PartDescription { get; set; } = "";
            public string WarehouseCode { get; set; } = "";
            public string BinNum { get; set; } = "";
            public string LotNum { get; set; } = "";
            public string IUM { get; set; } = "";
            public decimal OnhandQty { get; set; }
            public string FirstRefDate { get; set; } = "";
        }

        public class InveViewLocationResult
        {
            public string Unit { get; set; } = string.Empty;
            public string WarehouseCode { get; set; } = string.Empty;
            public string BinNum { get; set; } = string.Empty;
            public decimal OnhandQty { get; set; }
        }
    }
}