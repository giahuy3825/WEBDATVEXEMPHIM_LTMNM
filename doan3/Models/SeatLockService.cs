using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using StackExchange.Redis;

namespace doan3.Models
{
    public class SeatLockService
    {
        private static bool IsNoSQLEnabled => !string.Equals(ConfigurationManager.AppSettings["EnableNoSQL"], "false", StringComparison.OrdinalIgnoreCase);

        private static string GetKey(long lichChieuId, long gheId)
        {
            return $"seatlock:{lichChieuId}:{gheId}";
        }

        /// <summary>
        /// Lấy danh sách ID các ghế đang bị khóa cho suất chiếu này
        /// </summary>
        public static List<long> GetLockedSeatIds(long lichChieuId)
        {
            if (!IsNoSQLEnabled)
            {
                return new List<long>();
            }
            var redis = RedisService.Connection;
            var server = redis.GetServer(redis.GetEndPoints().First());
            var db = RedisService.GetDatabase();

            string pattern = $"seatlock:{lichChieuId}:*";
            var keys = server.Keys(database: 0, pattern: pattern).ToList();

            var lockedGheIds = new List<long>();
            foreach (var key in keys)
            {
                string keyStr = key.ToString();
                string[] parts = keyStr.Split(':');
                if (parts.Length == 3 && long.TryParse(parts[2], out long gheId))
                {
                    // Chỉ lấy nếu key còn sống (tồn tại trên Redis)
                    if (db.KeyExists(key))
                    {
                        lockedGheIds.Add(gheId);
                    }
                }
            }
            return lockedGheIds;
        }

        /// <summary>
        /// Khóa nguyên tử (Atomic Lock) tập hợp nhiều ghế trong Redis với TTL (mặc định 90s - 1.5 phút)
        /// </summary>
        public static bool LockSeats(long lichChieuId, List<long> gheIds, long khachHangId, int durationSeconds = 90)
        {
            if (!IsNoSQLEnabled) return true;

            var db = RedisService.GetDatabase();
            var acquiredLocks = new List<string>();

            foreach (var gheId in gheIds)
            {
                string key = GetKey(lichChieuId, gheId);
                // SETNX: Chỉ set nếu key chưa tồn tại
                bool locked = db.StringSet(key, khachHangId.ToString(), TimeSpan.FromSeconds(durationSeconds), When.NotExists);

                if (locked)
                {
                    acquiredLocks.Add(key);
                }
                else
                {
                    // Nếu 1 ghế bị trùng, Rollback giải phóng ngay các ghế đã lỡ khóa
                    foreach (var lockedKey in acquiredLocks)
                    {
                        db.KeyDelete(lockedKey);
                    }
                    return false; // Khóa thất bại
                }
            }

            return true; // Khóa thành công tất cả ghế
        }

        /// <summary>
        /// Kiểm tra tất cả các ghế có còn thuộc về khách hàng này không
        /// </summary>
        public static bool VerifySeatsLockedByCustomer(long lichChieuId, List<long> gheIds, long khachHangId)
        {
            if (!IsNoSQLEnabled) return true;

            var db = RedisService.GetDatabase();
            foreach (var gheId in gheIds)
            {
                string key = GetKey(lichChieuId, gheId);
                RedisValue val = db.StringGet(key);
                if (val.IsNullOrEmpty || val.ToString() != khachHangId.ToString())
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Lấy thời gian giữ ghế còn lại (tính bằng Giây)
        /// </summary>
        public static long GetRemainingLockTime(long lichChieuId, List<long> gheIds)
        {
            if (gheIds == null || !gheIds.Any() || !IsNoSQLEnabled) return 90;

            var db = RedisService.GetDatabase();
            long maxTtlSeconds = 0;

            foreach (var gheId in gheIds)
            {
                string key = GetKey(lichChieuId, gheId);
                TimeSpan? ttl = db.KeyTimeToLive(key);
                if (ttl.HasValue)
                {
                    long totalSec = (long)Math.Max(0, ttl.Value.TotalSeconds);
                    if (totalSec > maxTtlSeconds) maxTtlSeconds = totalSec;
                }
            }

            return maxTtlSeconds;
        }

        /// <summary>
        /// Giải phóng (xóa) khóa ghế trên Redis
        /// </summary>
        public static void ReleaseSeatLocks(long lichChieuId, List<long> gheIds)
        {
            if (gheIds == null || !gheIds.Any() || !IsNoSQLEnabled) return;

            var db = RedisService.GetDatabase();
            foreach (var gheId in gheIds)
            {
                string key = GetKey(lichChieuId, gheId);
                db.KeyDelete(key);
            }
        }
    }
}
