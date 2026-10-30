using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    public class Product
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string ProductCode { get; set; } = null!;

        [Required]
        [StringLength(100)]
        public string Barcode { get; set; } = null!;

        [Required]
        [StringLength(200)]
        public string ProductName { get; set; } = null!;

        [StringLength(500)]
        public string Description { get; set; } = null!;

        [StringLength(100)]
        public string Category { get; set; } = null!;

        [StringLength(50)]
        public string Unit { get; set; } = "个";

        [Range(0, 1000000)]
        public decimal Price { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.Now;

        [StringLength(500)]
        public string ImageUrl { get; set; } = null!;
    }
}
