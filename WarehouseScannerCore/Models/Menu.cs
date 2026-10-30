namespace WarehouseScannerCore.Models
{
    public class Menu
    {
        public string Code { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string NameCN { get; set; } = null!;
        public string Description { get; set; } = null!;
        public string DescriptionCN { get; set; } = null!;
        public string Location { get; set; } = null!;
        public string ApiEndpoint { get; set; } = null!;
        public string Icon { get; set; } = null!;
        public string Timezone { get; set; } = null!;
        public bool Enabled { get; set; }
    }
}
