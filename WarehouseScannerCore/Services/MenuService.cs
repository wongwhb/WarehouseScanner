// Services/MenuService.cs
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

namespace WarehouseScannerCore.Services
{
    public interface IMenuService
    {
        Task<List<SelectListItem>> GetMenuSelectItemsAsync();
    }

    public class MenuService : IMenuService
    {
        private readonly ILogger<MenuService>? _logger;
        private readonly IWebHostEnvironment? _env;
        private readonly IMemoryCache? _cache;

        private const string MenuCacheKey = "MenuService:MenuItems";

        public MenuService(ILogger<MenuService>? logger = null, IWebHostEnvironment? env = null, IMemoryCache? cache = null)
        {
            _logger = logger;
            _env = env;
            _cache = cache;
        }

        public Task<List<SelectListItem>> GetMenuSelectItemsAsync()
        {
            if (_cache == null)
                return LoadMenuItemsAsync();

            return _cache.GetOrCreateAsync(MenuCacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
                return LoadMenuItemsAsync();
            })!;
        }

        private async Task<List<SelectListItem>> LoadMenuItemsAsync()
        {
            var menuItems = new List<SelectListItem>();

            try
            {
                var baseDir = _env?.ContentRootPath ?? Directory.GetCurrentDirectory();
                var configPath = Path.Combine(baseDir, "config", "Menu.json");

                if (File.Exists(configPath))
                {
                    var json = await File.ReadAllTextAsync(configPath);
                    var menuConfig = JsonConvert.DeserializeObject<MenuConfig>(json);

                    if (menuConfig?.Menu != null)
                    {
                        foreach (var menu in menuConfig.Menu)
                        {
                            menuItems.Add(new SelectListItem
                            {
                                Value = menu.Code,
                                Text = $"{menu.Code} - {menu.NameCN}"
                            });
                        }
                    }
                }
                else
                {
                    _logger?.LogWarning("Menu.json not found at: {Path}", configPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "加载菜单失败");
            }

            if (menuItems.Count == 0)
            {
                menuItems.Add(new SelectListItem { Value = "PICKING_SCAN", Text = "PICKING_SCAN - 发料扫码" });
                menuItems.Add(new SelectListItem { Value = "OUTBOUND_SCAN", Text = "OUTBOUND_SCAN - 出库扫码" });
                menuItems.Add(new SelectListItem { Value = "TRANSFER_SCAN", Text = "TRANSFER_SCAN - 库存转移扫码" });
                menuItems.Add(new SelectListItem { Value = "INBOUND_SCAN", Text = "INBOUND_SCAN - 入库扫码" });
                menuItems.Add(new SelectListItem { Value = "COUNT_SCAN", Text = "COUNT_SCAN - 盘点扫码" });
                menuItems.Add(new SelectListItem { Value = "SYSTEM_SETTINGS", Text = "SYSTEM_SETTINGS - 系统设置" });
            }

            return menuItems;
        }

        private class MenuConfig
        {
            public List<MenuItem> Menu { get; set; } = null!;
        }

        private class MenuItem
        {
            public string Code { get; set; } = null!;
            public string NameCN { get; set; } = null!;
        }
    }
}
