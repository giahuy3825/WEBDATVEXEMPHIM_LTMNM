using System;
using Cassandra;
using doan3.Models.Cass.DTO;

namespace doan3.Models.Cass
{
    /// <summary>
    /// ============================================================================
    /// MODULE CASSANDRA - HOÀN TOÀN ĐỘC LẬP
    /// ============================================================================
    /// Cung cấp 3 chức năng ghi Log/Timeline/History xuống Cassandra:
    ///   1. GhiLichSuGhe        -> bảng lich_su_ghe        (Seat Reservation Timeline)
    ///   2. GhiLichSuDatVe      -> bảng lich_su_dat_ve     (Booking Lifecycle)
    ///   3. GhiNhatKyHoatDong   -> bảng nhat_ky_hoat_dong  (User Activity Log)
    ///
    /// NGUYÊN TẮC BẮT BUỘC:
    ///   - Chỉ gọi SAU KHI SQL Server đã Commit thành công.
    ///   - KHÔNG đặt trong khối BeginTransaction() của SQL Server.
    ///   - Mọi lỗi Cassandra đều được try/catch tại đây — KHÔNG THROW ngược lên
    ///     Controller để tránh crash chức năng nghiệp vụ chính.
    ///   - Lỗi kỹ thuật được ghi vào Debug Output để developer theo dõi.
    ///   - Chỉ INSERT, không UPDATE/DELETE (đúng chuẩn append-only time-series).
    /// ============================================================================
    /// </summary>
    public static class CassandraFeaturesService
    {
        private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

        /// <summary>Giờ Việt Nam hiện tại (UTC+7), dùng thống nhất cho cả 3 bảng.</summary>
        private static DateTimeOffset GioVietNamHienTai()
        {
            return new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero).ToOffset(VietnamOffset);
        }

        // ==========================================================================
        // CHỨC NĂNG 1: SEAT RESERVATION TIMELINE
        // Bảng: lich_su_ghe
        // Partition Key: (lich_chieu_id, ghe_id) | Clustering: thoi_gian ASC, id ASC
        // Vòng đời: LOCK -> BOOKED -> CHECK_IN  hoặc  LOCK -> CANCEL
        // ==========================================================================
        public static void GhiLichSuGhe(LichSuGheDTO data)
        {
            if (data == null) return;

            try
            {
                var session = CassandraService.GetSession();
                if (session == null) return;

                const string cql = @"
                    INSERT INTO lich_su_ghe
                    (
                        lich_chieu_id, ghe_id,
                        thoi_gian, id,
                        trang_thai, khach_hang_id, don_dat_ve_id, ghi_chu,
                        controller_name, action_name, request_method,
                        browser, device, he_dieu_hanh, ip_address, ket_qua
                    )
                    VALUES
                    (
                        ?, ?,
                        ?, ?,
                        ?, ?, ?, ?,
                        ?, ?, ?,
                        ?, ?, ?, ?, ?
                    )";

                var stmt = new SimpleStatement(
                    cql,
                    data.LichChieuId,
                    data.GheId,
                    GioVietNamHienTai(),
                    Guid.NewGuid(),
                    data.TrangThai ?? "",
                    data.KhachHangId,
                    data.DonDatVeId,
                    data.GhiChu ?? "",
                    data.ControllerName ?? "",
                    data.ActionName ?? "",
                    data.RequestMethod ?? "",
                    data.Browser ?? "",
                    data.Device ?? "",
                    data.HeDieuHanh ?? "",
                    data.IpAddress ?? "",
                    data.KetQua ?? "SUCCESS"
                );

                session.Execute(stmt);

                System.Diagnostics.Debug.WriteLine(
                    $"[Cassandra][lich_su_ghe] LichChieuID={data.LichChieuId} GheID={data.GheId} TrangThai={data.TrangThai}");
            }
            catch (Exception ex)
            {
                // BẮT BUỘC: không re-throw — Cassandra lỗi không được làm crash transaction SQL chính
                System.Diagnostics.Debug.WriteLine(
                    $"[Cassandra][lich_su_ghe] Bo qua loi ghi log: {ex.Message}");
            }
        }

        // ==========================================================================
        // CHỨC NĂNG 2: BOOKING LIFECYCLE LOG
        // Bảng: lich_su_dat_ve
        // Partition Key: khach_hang_id | Clustering: thoi_gian DESC, id ASC
        // ==========================================================================
        public static void GhiLichSuDatVe(LichSuDatVeDTO data)
        {
            if (data == null) return;

            try
            {
                var session = CassandraService.GetSession();
                if (session == null) return;

                const string cql = @"
                    INSERT INTO lich_su_dat_ve
                        (khach_hang_id, thoi_gian, id,
                         don_dat_ve_id, ma_dat_ve, buoc,
                         lich_chieu_id, so_ghe, tong_tien, ghi_chu)
                    VALUES
                        (?, ?, ?,
                         ?, ?, ?,
                         ?, ?, ?, ?)";

                var stmt = new SimpleStatement(
                    cql,
                    data.KhachHangId,
                    GioVietNamHienTai(),
                    Guid.NewGuid(),
                    data.DonDatVeId,
                    data.MaDatVe ?? "",
                    data.Buoc ?? "",
                    data.LichChieuId,
                    data.SoGhe,
                    data.TongTien,
                    data.GhiChu ?? ""
                );

                session.Execute(stmt);

                System.Diagnostics.Debug.WriteLine(
                    $"[Cassandra][lich_su_dat_ve] KhachHangID={data.KhachHangId} Buoc={data.Buoc}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Cassandra][lich_su_dat_ve] Bo qua loi ghi log: {ex.Message}");
            }
        }

        // ==========================================================================
        // CHỨC NĂNG 3: USER ACTIVITY LOG
        // Bảng: nhat_ky_hoat_dong
        // Partition Key: (username, ngay) | Clustering: thoi_gian DESC, id ASC
        // Bucket theo NGÀY (VN UTC+7) để chống Hot Partition.
        //
        // HanhDong chuẩn: REGISTER | LOGIN | LOGOUT | SEARCH_MOVIE |
        //   VIEW_MOVIE_DETAIL | VIEW_MOVIE_BY_GENRE | VIEW_CINEMA | VIEW_SHOWTIME |
        //   LOCK_SEAT | BOOK_SEAT | CANCEL_SEAT |
        //   PAYMENT_REQUEST | OTP_REQUEST | PAYMENT_SUCCESS | PAYMENT_FAILED |
        //   CHANGE_PASSWORD
        // KetQua: SUCCESS | FAILED
        // ==========================================================================
        public static void GhiNhatKyHoatDong(NhatKyHoatDongDTO data)
        {
            if (data == null) return;

            try
            {
                var session = CassandraService.GetSession();
                if (session == null) return;
                var gioVn = GioVietNamHienTai();
                // Bucket theo ngày VN để tránh hot partition
                string ngay = gioVn.ToString("yyyy-MM-dd");

                const string cql = @"
                    INSERT INTO nhat_ky_hoat_dong
                    (
                        username, ngay,
                        thoi_gian, id,
                        hanh_dong, ket_qua, chi_tiet,
                        ip_address,
                        controller_name, action_name, request_method,
                        browser, device, he_dieu_hanh
                    )
                    VALUES
                    (
                        ?, ?,
                        ?, ?,
                        ?, ?, ?,
                        ?,
                        ?, ?, ?,
                        ?, ?, ?
                    )";

                string username = string.IsNullOrWhiteSpace(data.Username)
                    ? "khach_vang_lai"
                    : data.Username.Trim().ToLower();

                var stmt = new SimpleStatement(
                    cql,
                    username,
                    ngay,
                    gioVn,
                    Guid.NewGuid(),
                    data.HanhDong ?? "",
                    data.KetQua ?? "SUCCESS",
                    data.ChiTiet ?? "",
                    data.IpAddress ?? "",
                    data.ControllerName ?? "",
                    data.ActionName ?? "",
                    data.RequestMethod ?? "",
                    data.Browser ?? "",
                    data.Device ?? "",
                    data.HeDieuHanh ?? ""
                );

                session.Execute(stmt);

                System.Diagnostics.Debug.WriteLine(
                    $"[Cassandra][nhat_ky_hoat_dong] {username} | {data.HanhDong} | {data.KetQua}");
            }
            catch (Exception ex)
            {
                // BẮT BUỘC: không re-throw — lỗi Cassandra không được crash app
                System.Diagnostics.Debug.WriteLine(
                    $"[Cassandra][nhat_ky_hoat_dong] Bo qua loi ghi log: {ex.Message}");
            }
        }
    }
}
