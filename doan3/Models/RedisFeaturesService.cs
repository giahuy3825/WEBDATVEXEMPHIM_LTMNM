using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using StackExchange.Redis;

namespace doan3.Models
{
    public class CartItemDTO
    {
        public string Username { get; set; }
        public long LichChieuId { get; set; }
        public string DanhSachGhe { get; set; }
        public decimal TongTien { get; set; }
        public string ThoiGianTao { get; set; }
        public long RemainingTtlSeconds { get; set; }
    }

    public class UserSessionDTO
    {
        public string Username { get; set; }
        public int UserId { get; set; }
        public string FullName { get; set; }
        public string RoleName { get; set; }
        public string LastAccess { get; set; }
    }

    public class RedisFeaturesService
    {
        private static bool IsNoSQLEnabled => !string.Equals(ConfigurationManager.AppSettings["EnableNoSQL"], "false", StringComparison.OrdinalIgnoreCase);

        // =========================================================================
        // TÍNH NĂNG 2: GIỎ HÀNG THONG TIN THANH TOÁN (Hash + TTL 600s / 10 phút)
        // Key dạng: cart:{username}
        // =========================================================================
        public static bool SaveCart(string username, long lichChieuId, string seatIds, decimal totalAmount, int ttlSeconds = 600)
        {
            if (string.IsNullOrEmpty(username) || !IsNoSQLEnabled) return false;

            var db = RedisService.GetDatabase();
            string cartKey = $"cart:{username.ToLower().Trim()}";

            var hashEntries = new HashEntry[]
            {
                new HashEntry("lichChieuId", lichChieuId.ToString()),
                new HashEntry("danhSachGhe", seatIds ?? ""),
                new HashEntry("tongTien", totalAmount.ToString()),
                new HashEntry("thoiGianTao", DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"))
            };

            db.HashSet(cartKey, hashEntries);
            return db.KeyExpire(cartKey, TimeSpan.FromSeconds(ttlSeconds));
        }

        public static CartItemDTO GetCart(string username)
        {
            if (string.IsNullOrEmpty(username) || !IsNoSQLEnabled) return null;

            var db = RedisService.GetDatabase();
            string cartKey = $"cart:{username.ToLower().Trim()}";

            if (!db.KeyExists(cartKey)) return null;

            var hash = db.HashGetAll(cartKey).ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());
            TimeSpan? ttl = db.KeyTimeToLive(cartKey);

            return new CartItemDTO
            {
                Username = username,
                LichChieuId = hash.ContainsKey("lichChieuId") && long.TryParse(hash["lichChieuId"], out long lcId) ? lcId : 0,
                DanhSachGhe = hash.ContainsKey("danhSachGhe") ? hash["danhSachGhe"] : "",
                TongTien = hash.ContainsKey("tongTien") && decimal.TryParse(hash["tongTien"], out decimal val) ? val : 0,
                ThoiGianTao = hash.ContainsKey("thoiGianTao") ? hash["thoiGianTao"] : "",
                RemainingTtlSeconds = ttl.HasValue ? (long)ttl.Value.TotalSeconds : 0
            };
        }

        public static bool ClearCart(string username)
        {
            if (string.IsNullOrEmpty(username) || !IsNoSQLEnabled) return false;
            var db = RedisService.GetDatabase();
            string cartKey = $"cart:{username.ToLower().Trim()}";
            return db.KeyDelete(cartKey);
        }

        // =========================================================================
        // TÍNH NĂNG 3: MÃ OTP THANH TOÁN (String + TTL 120s / 2 phút)
        // Key dạng: otp:checkout:{username}
        // =========================================================================
        public static string GenerateOtp(string username, int ttlSeconds = 120)
        {
            if (string.IsNullOrEmpty(username)) return null;
            if (!IsNoSQLEnabled) return "123456";

            var db = RedisService.GetDatabase();
            string otpKey = $"otp:checkout:{username.ToLower().Trim()}";

            Random rand = new Random();
            string otpCode = rand.Next(100000, 999999).ToString();

            // Set mã OTP vào Redis với thời gian sống 120s
            db.StringSet(otpKey, otpCode, TimeSpan.FromSeconds(ttlSeconds));
            return otpCode;
        }

        public static bool VerifyOtp(string username, string inputOtp)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(inputOtp)) return false;
            if (!IsNoSQLEnabled) return true;

            var db = RedisService.GetDatabase();
            string otpKey = $"otp:checkout:{username.ToLower().Trim()}";

            RedisValue storedOtp = db.StringGet(otpKey);

            if (storedOtp.HasValue && storedOtp.ToString() == inputOtp.Trim())
            {
                // OTP khớp -> Xóa OTP khỏi Redis để không dùng lại được lần 2 (Anti-replay)
                db.KeyDelete(otpKey);
                return true;
            }

            return false;
        }

        public static long GetRemainingOtpTtl(string username)
        {
            if (string.IsNullOrEmpty(username) || !IsNoSQLEnabled) return 0;
            var db = RedisService.GetDatabase();
            string otpKey = $"otp:checkout:{username.ToLower().Trim()}";
            TimeSpan? ttl = db.KeyTimeToLive(otpKey);
            return ttl.HasValue ? (long)Math.Max(0, ttl.Value.TotalSeconds) : 0;
        }

        // =========================================================================
        // TÍNH NĂNG 4: LƯU REDIS SESSION USER (Hash + TTL 1800s / 30 phút)
        // Key dạng: session:user:{username}
        // =========================================================================
        public static bool SaveUserSession(string username, int userId, string fullName, string roleName, int ttlSeconds = 1800)
        {
            if (string.IsNullOrEmpty(username) || !IsNoSQLEnabled) return false;

            var db = RedisService.GetDatabase();
            string sessionKey = $"session:user:{username.ToLower().Trim()}";

            var hashEntries = new HashEntry[]
            {
                new HashEntry("userId", userId.ToString()),
                new HashEntry("username", username),
                new HashEntry("fullName", fullName ?? ""),
                new HashEntry("roleName", roleName ?? "Customer"),
                new HashEntry("lastAccess", DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"))
            };

            db.HashSet(sessionKey, hashEntries);
            return db.KeyExpire(sessionKey, TimeSpan.FromSeconds(ttlSeconds));
        }

        public static UserSessionDTO GetUserSession(string username)
        {
            if (string.IsNullOrEmpty(username) || !IsNoSQLEnabled) return null;

            var db = RedisService.GetDatabase();
            string sessionKey = $"session:user:{username.ToLower().Trim()}";

            if (!db.KeyExists(sessionKey)) return null;

            var hash = db.HashGetAll(sessionKey).ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

            return new UserSessionDTO
            {
                Username = username,
                UserId = hash.ContainsKey("userId") && int.TryParse(hash["userId"], out int uId) ? uId : 0,
                FullName = hash.ContainsKey("fullName") ? hash["fullName"] : "",
                RoleName = hash.ContainsKey("roleName") ? hash["roleName"] : "",
                LastAccess = hash.ContainsKey("lastAccess") ? hash["lastAccess"] : ""
            };
        }

        public static bool RemoveUserSession(string username)
        {
            if (string.IsNullOrEmpty(username) || !IsNoSQLEnabled) return false;
            var db = RedisService.GetDatabase();
            string sessionKey = $"session:user:{username.ToLower().Trim()}";
            return db.KeyDelete(sessionKey);
        }
    }
}
