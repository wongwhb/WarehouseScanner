using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace WarehouseScannerCore.Utilities
{
    public static class SessionExtensions
    {
        public static void Set<T>(this ISession session, string key, T value)
        {
            session.SetString(key, JsonConvert.SerializeObject(value));
        }

        public static T? Get<T>(this ISession session, string key)
        {
            var value = session.GetString(key);
            return value == null ? default : JsonConvert.DeserializeObject<T>(value);
        }
    }

    public class SessionCleanupHelper
    {
        public static void ClearUserSession(HttpContext context, ILogger? logger = null)
        {
            if (context == null) return;

            try
            {
                context.Session.Clear();
                context.Response.Cookies.Delete(".AspNetCore.Session");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Session清理失败");
                throw;
            }
        }

        public static bool IsSessionValid(HttpContext context)
        {
            if (context == null) return false;
            if (!context.User.Identity!.IsAuthenticated)
                return false;
            var userId = context.Session.GetString(SessionKeys.UserId);
            return !string.IsNullOrEmpty(userId);
        }

        public static void SetSessionTimeout(HttpContext context, int minutes = 30)
        {
            if (context == null) return;
            context.Session.SetInt32("SessionTimeout", minutes);
        }
    }
}