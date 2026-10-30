using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WarehouseScannerCore.Services
{
    /// <summary>
    /// AES 加密辅助服务 —— 密钥通过 IConfiguration 读取
    ///
    /// 配置优先级（高 → 低）：
    ///   1. 环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV
    ///   2. appsettings.json "Encryption:Key" / "Encryption:IV"
    ///
    /// 开发环境可在 appsettings.Development.json 中配置，
    /// 生产环境建议使用环境变量或 Key Vault。
    /// </summary>
    public class EncryptionHelper
    {
        private const string KnownDefaultKey = "b14ca5898a4e4133bbce2ea2315a1916";
        private const string KnownDefaultIV = "8081828384858687";

        /// <summary>
        /// 曾提交进源码仓库的 AES 密钥（已泄露，仅限本地开发使用）。
        /// 生产环境命中该值时拒绝启动，避免带着公开密钥加密的凭据继续运行。
        /// </summary>
        private const string LeakedKey = "WarehouseScanner2026AES256Key01Z";
        private const string LeakedIV = "WareScanIV2026!!";

        /// <summary>
        /// 新格式密文前缀标记（v2：随机 IV + 密文）。
        /// 以该前缀开头的密文按新格式解析；无前缀的历史密文按旧格式（固定 IV）解析。
        /// 前缀字符 ":" 不在 Base64 字符集中，可与旧格式 Base64 密文确定性区分。
        /// </summary>
        private const string NewFormatPrefix = "v2:";

        private readonly string? _key;
        private readonly string? _iv;
        private byte[] _keyBytes = null!;
        private byte[] _ivBytes = null!;
        private readonly ILogger<EncryptionHelper> _logger;

        public EncryptionHelper(IConfiguration configuration, ILogger<EncryptionHelper> logger)
        {
            _logger = logger;

            (_key, _iv) = ReadConfiguredKeyMaterial(configuration);

            ValidateKeyMaterial();
            NormalizeKeyMaterial();
        }

        /// <summary>
        /// 读取配置中的 AES 密钥材料。
        /// 优先级（高 → 低）：环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV → 配置节 Encryption:Key / Encryption:IV。
        /// </summary>
        private static (string? Key, string? IV) ReadConfiguredKeyMaterial(IConfiguration configuration)
            => (configuration["AES_ENCRYPTION_KEY"] ?? configuration["Encryption:Key"],
                configuration["AES_ENCRYPTION_IV"] ?? configuration["Encryption:IV"]);

        /// <summary>
        /// 启动期校验：非开发环境下缺少 Key/IV 或使用已泄露密钥时直接抛异常拒绝启动。
        /// 密钥缺失会使已加密的 Epicor 凭据无法解密，应用即使启动也无法完成认证；
        /// 使用已提交进仓库的密钥则等于凭据无保护。两者都属于不可运行状态，因此采用 fail-fast。
        /// </summary>
        public static void EnsureKeyMaterialOrThrow(IConfiguration configuration, bool isNonDevelopmentEnvironment)
        {
            if (!isNonDevelopmentEnvironment) return;

            var (key, iv) = ReadConfiguredKeyMaterial(configuration);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(iv))
            {
                throw new InvalidOperationException(
                    "生产环境缺少 AES 加密密钥，拒绝启动。请通过环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV，" +
                    "或配置节 Encryption:Key / Encryption:IV 提供密钥。");
            }

            if (IsKnownInsecureKeyMaterial(key, iv))
            {
                throw new InvalidOperationException(
                    "生产环境使用了曾提交进源码仓库的 AES 密钥，拒绝启动。请通过环境变量 " +
                    "AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV 注入新密钥，并轮换已加密的 Epicor ApiKey。");
            }
        }

        /// <summary>
        /// 判断密钥材料是否属于已知不安全值（源码仓库泄露值或早期示例默认值）。
        /// </summary>
        private static bool IsKnownInsecureKeyMaterial(string? key, string? iv)
            => string.Equals(key, LeakedKey, StringComparison.Ordinal)
               || string.Equals(iv, LeakedIV, StringComparison.Ordinal)
               || string.Equals(key, KnownDefaultKey, StringComparison.Ordinal)
               || string.Equals(iv, KnownDefaultIV, StringComparison.Ordinal);

        /// <summary>
        /// 将 Key/IV 自动校准为 AES 合法长度：
        ///   Key:  16 / 24 / 32 字节（AES-128 / 192 / 256）
        ///   IV:   16 字节（块大小）
        /// 过长截断，过短用 0x00 填充。
        /// </summary>
        private void NormalizeKeyMaterial()
        {
            _keyBytes = NormalizeToAesKeySize(Encoding.UTF8.GetBytes(_key ?? ""), "Key");
            _ivBytes = NormalizeToExactSize(Encoding.UTF8.GetBytes(_iv ?? ""), 16, "IV");
        }

        private static byte[] NormalizeToAesKeySize(byte[] input, string name)
        {
            int target;
            if (input.Length <= 16) target = 16;
            else if (input.Length <= 24) target = 24;
            else target = 32;

            return PadOrTruncate(input, target, name);
        }

        private static byte[] NormalizeToExactSize(byte[] input, int target, string name)
            => PadOrTruncate(input, target, name);

        private static byte[] PadOrTruncate(byte[] input, int target, string name)
        {
            if (input.Length == target) return input;

            var result = new byte[target];
            int copyLen = Math.Min(input.Length, target);
            Buffer.BlockCopy(input, 0, result, 0, copyLen);
            return result;
        }

        private void ValidateKeyMaterial()
        {
            if (string.IsNullOrWhiteSpace(_key) || string.IsNullOrWhiteSpace(_iv))
            {
                _logger.LogWarning(
                    "AES 加密密钥未配置 —— 加密/解密功能将不可用。" +
                    "请通过环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV " +
                    "或 appsettings.json Encryption:Key / Encryption:IV 配置。");
            }

            if (IsKnownInsecureKeyMaterial(_key, _iv))
            {
                _logger.LogWarning(
                    "AES 加密密钥为曾提交进源码仓库的公开值，仅限本地开发使用；生产环境将拒绝启动。" +
                    "请通过环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV 注入新密钥。");
            }
        }

        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(_key) && !string.IsNullOrWhiteSpace(_iv);

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            if (!IsAvailable)
            {
                throw new InvalidOperationException(
                    "无法加密：AES 加密密钥未配置。请设置环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV。");
            }

            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Key = _keyBytes;
                aesAlg.GenerateIV(); // 每次加密使用随机 IV，避免固定 IV 导致确定性加密

                ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                        {
                            swEncrypt.Write(plainText);
                        }
                    }

                    // 密文格式："v2:" + Base64(随机 IV(16字节) + 密文)
                    // 前缀用于与旧格式（固定 IV 的纯 Base64 密文）确定性区分
                    byte[] cipherBytes = msEncrypt.ToArray();
                    byte[] result = new byte[aesAlg.IV.Length + cipherBytes.Length];
                    Buffer.BlockCopy(aesAlg.IV, 0, result, 0, aesAlg.IV.Length);
                    Buffer.BlockCopy(cipherBytes, 0, result, aesAlg.IV.Length, cipherBytes.Length);
                    return NewFormatPrefix + Convert.ToBase64String(result);
                }
            }
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return cipherText;

            if (!IsAvailable)
            {
                throw new InvalidOperationException(
                    "无法解密：AES 加密密钥未配置。请设置环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV。");
            }

            // 密文格式：
            //   新格式："v2:" + Base64(随机 IV(16字节) + 密文)，由当前 Encrypt 方法产生
            //   旧格式：Base64(固定 IV 加密的整段密文) —— 现有 factories.json / 历史数据
            // 通过 NewFormatPrefix 前缀确定性区分，无需启发式判断。
            if (cipherText.StartsWith(NewFormatPrefix, StringComparison.Ordinal))
            {
                string b64 = cipherText.Substring(NewFormatPrefix.Length);
                byte[] full = Convert.FromBase64String(b64);
                if (full.Length <= 16)
                    throw new CryptographicException("新格式密文长度不足（需包含 16 字节 IV + 密文）");

                byte[] iv = new byte[16];
                Buffer.BlockCopy(full, 0, iv, 0, 16);
                byte[] cipherBytes = new byte[full.Length - 16];
                Buffer.BlockCopy(full, 16, cipherBytes, 0, cipherBytes.Length);
                return DecryptCore(cipherBytes, iv);
            }

            // 旧格式：固定 IV 解密整段
            byte[] oldFull = Convert.FromBase64String(cipherText);
            return DecryptCore(oldFull, _ivBytes);
        }

        private string DecryptCore(byte[] cipherBytes, byte[] iv)
        {
            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Key = _keyBytes;
                aesAlg.IV = iv;

                using (MemoryStream msDecrypt = new MemoryStream(cipherBytes))
                {
                    using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, aesAlg.CreateDecryptor(aesAlg.Key, iv), CryptoStreamMode.Read))
                    {
                        using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                        {
                            return srDecrypt.ReadToEnd();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 尝试解密；如果 cipherText 是明文（Base64 解码失败或 AES 填充错误）则原样返回。
        /// 用于从 v1（明文存储 ApiKey/密码）平滑迁移到 v2（加密存储）。
        /// </summary>
        public string TryDecrypt(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text!;
            if (!IsAvailable)
            {
                // 加密密钥未配置时，检测值是否可能为密文（Base64 且长度 >= 24），
                // 若是则告警，避免密文被当作明文直接使用导致下游 401/认证失败。
                if (text.Length >= 24)
                {
                    Span<byte> probe = stackalloc byte[text.Length];
                    if (Convert.TryFromBase64String(text, probe, out _))
                    {
                        _logger.LogError(
                            "TryDecrypt: 加密密钥未配置，值可能为密文但无法解密（长度 {Len}，疑似 Base64）。" +
                            "请配置环境变量 AES_ENCRYPTION_KEY / AES_ENCRYPTION_IV 或 appsettings.json Encryption 节。",
                            text.Length);
                    }
                }
                return text;
            }

            // 新格式密文带 "v2:" 前缀，需去掉前缀后再做 Base64 校验
            bool isNewFormat = text.StartsWith(NewFormatPrefix, StringComparison.Ordinal);
            string b64Part = isNewFormat ? text.Substring(NewFormatPrefix.Length) : text;

            // 加密后的内容必然是 Base64 字符串；先用长度做一次快速过滤——AES-CBC 密文至少 16 字节。
            // 明显不满足的直接视为明文返回，避免无谓的解码开销。
            if (b64Part.Length < 24)
                return text;
            // 使用 Convert.TryFromBase64String 校验是否为合法 Base64（替代手工字符扫描，P3-consistency）
            Span<byte> buffer = stackalloc byte[b64Part.Length];
            if (!Convert.TryFromBase64String(b64Part, buffer, out _))
                return text;

            try
            {
                return Decrypt(text);
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException || ex is ArgumentException)
            {
                // 解密失败 → 视为明文原值返回
                return text;
            }
        }

        public static string GenerateKey()
        {
            using (Aes aes = Aes.Create())
            {
                aes.GenerateKey();
                return Convert.ToBase64String(aes.Key);
            }
        }
    }
}
