using Newtonsoft.Json;

namespace WarehouseScannerCore.Models
{
    public class DatabaseConfig
    {
        [JsonProperty("encryptedConnectionString")]
        public string EncryptedConnectionString { get; set; } = null!;

        [JsonIgnore]
        public string ConnectionString { get; set; } = null!;
    }
}
