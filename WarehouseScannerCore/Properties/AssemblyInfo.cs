using System.Runtime.Versioning;

// 本应用仅部署于 Windows：SilentPrintService 使用 System.Drawing.Printing，
// BarcodeController.Helpers 使用 System.Drawing 位图 API。
// 程序集级平台声明使 CA1416 分析按 Windows 目标判定，消除各调用点警告噪音。
[assembly: SupportedOSPlatform("windows")]
