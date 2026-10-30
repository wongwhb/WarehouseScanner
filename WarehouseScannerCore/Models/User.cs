using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseScannerCore.Models
{
    public class User
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Username { get; set; } = null!;

        [Required]
        [StringLength(100)]
        public string Password { get; set; } = null!;

        [StringLength(100)]
        public string FullName { get; set; } = null!;

        [StringLength(50)]
        public string Role { get; set; } = "User";

        public DateTime CreatedDate { get; set; } = DateTime.Now;

        [StringLength(10)]
        public string Language { get; set; } = "zh-CN";
       
    }
}
