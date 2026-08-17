using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Entity;
using System.Linq;
using System.Web.Mvc;
using doan3.Models;
using doan3.Models.Cass;
using doan3.Models.Cass.DTO;

namespace doan3.Controllers
{
    public class ThanhToanController : Controller
    {
        private LTW_DatVeXemPhimEntities db = new LTW_DatVeXemPhimEntities();

        // =====================================================================
        // GET: /ThanhToan/Index
        // =====================================================================
        public ActionResult Index(long lichChieuId, string lockedSeatIds)
        {
            if (Session["USER_SESSION"] == null)
                return RedirectToAction("Index_DangNhap", "Login");

            var lichChieu = LayThongTinLichChieu(lichChieuId);
            if (lichChieu == null) return HttpNotFound();

            var danhSachIdGhe = TachChuoiIdGhe(lockedSeatIds);
            var danhSachGheHienThi = LayThongTinGhe(danhSachIdGhe);
            var soGiayConLai = TinhThoiGianGiuGheConLai(lichChieuId, danhSachIdGhe);
            var khachHangId = LayMaKhachHangTuSession();

            var diemHienCo = 0;
            if (khachHangId.HasValue)
                diemHienCo = LayDiemThanhVien(khachHangId.Value);

            var sessionUserObj = Session["USER_SESSION"] as UserLogin;
            if (sessionUserObj != null)
            {
                var userVouchers = doan3.Models.Mgdb.MgdbService.GetUserClaimedVouchers(sessionUserObj.UserName);
                ViewBag.UserClaimedVouchers = userVouchers;
            }

            decimal tongTienGoc = danhSachGheHienThi.Sum(s => s.GiaTien.GetValueOrDefault());
            decimal discount = 0;
            string appliedCode = Session["APPLIED_VOUCHER_CODE"] as string;
            if (!string.IsNullOrEmpty(appliedCode))
            {
                var promo = doan3.Models.Mgdb.MgdbService.GetPromotionByCode(appliedCode);
                if (promo != null)
                {
                    discount = promo.DiscountAmount;
                    ViewBag.AppliedVoucherCode = promo.Code;
                    ViewBag.VoucherDiscount = discount;
                }
            }

            decimal tongTienSauGiam = Math.Max(0, tongTienGoc - discount);

            var model = new ThanhToanViewModel
            {
                LichChieuId = lichChieuId,
                LockedSeatIds = lockedSeatIds,
                TenPhim = lichChieu.Phim.TenPhim,
                TenRap = lichChieu.Phong_Chieu.Rap_Chieu.TenRap,
                PhongChieu = lichChieu.Phong_Chieu.TenPhong,
                SuatChieu = lichChieu.ThoiGianBatDau.HasValue
                                    ? lichChieu.ThoiGianBatDau.Value.ToString("HH:mm dd/MM/yyyy") : "",
                DanhSachGhe = string.Join(", ", danhSachGheHienThi.Select(s => s.MaGhe)),
                TongTien = tongTienSauGiam,
                SoGiayConLai = soGiayConLai,
                DiemThanhVien = diemHienCo
            };

            if (sessionUserObj != null)
                RedisFeaturesService.SaveCart(sessionUserObj.UserName, lichChieuId, model.DanhSachGhe, model.TongTien, 600);

            return View(model);
        }

        // =====================================================================
        // POST: /ThanhToan/ChonVoucher
        // =====================================================================
        [HttpPost]
        public ActionResult ChonVoucher(string selectedVoucherCode, long lichChieuId, string lockedSeatIds)
        {
            if (string.IsNullOrEmpty(selectedVoucherCode) || selectedVoucherCode == "NONE")
            {
                Session.Remove("APPLIED_VOUCHER_CODE");
                TempData["Message"] = "Đã bỏ chọn mã Voucher giảm giá.";
            }
            else
            {
                var promo = doan3.Models.Mgdb.MgdbService.GetPromotionByCode(selectedVoucherCode);
                if (promo != null)
                {
                    Session["APPLIED_VOUCHER_CODE"] = promo.Code;
                    TempData["Message"] = "Đã tích chọn mã Voucher MongoDB " + promo.Code
                        + " (Giảm " + string.Format("{0:N0}", promo.DiscountAmount) + " VNĐ)!";
                }
                else
                {
                    TempData["Error"] = "Mã Voucher này không hợp lệ hoặc đã hết hạn!";
                }
            }
            return RedirectToAction("Index", new { lichChieuId, lockedSeatIds });
        }

        // =====================================================================
        // POST: /ThanhToan/ApDungVoucher
        // =====================================================================
        [HttpPost]
        public ActionResult ApDungVoucher(string voucherCode, long lichChieuId, string lockedSeatIds)
        {
            if (!string.IsNullOrEmpty(voucherCode))
            {
                var promo = doan3.Models.Mgdb.MgdbService.GetPromotionByCode(voucherCode);
                if (promo != null)
                {
                    Session["APPLIED_VOUCHER_CODE"] = promo.Code;
                    TempData["Message"] = "Đã áp dụng thành công mã Voucher MongoDB " + promo.Code
                        + " (Giảm " + string.Format("{0:N0}", promo.DiscountAmount) + " VNĐ)!";
                }
                else
                {
                    TempData["Error"] = "Mã Voucher này không tồn tại hoặc đã hết hạn trên MongoDB!";
                }
            }
            return RedirectToAction("Index", new { lichChieuId, lockedSeatIds });
        }

        // =====================================================================
        // POST: /ThanhToan/CancelTransaction
        //
        // Log Cassandra:
        //   - lich_su_ghe:        mỗi ghế 1 dòng trang_thai = "CANCEL"
        //   - nhat_ky_hoat_dong:  CANCEL_SEAT SUCCESS
        // =====================================================================
        [HttpPost]
        public ActionResult CancelTransaction(long lichChieuId, string lockedSeatIds)
        {
            var sessionUserObj = Session["USER_SESSION"] as UserLogin;
            var danhSachIdGhe = TachChuoiIdGhe(lockedSeatIds);

            string browser = Request.Browser.Browser + " " + Request.Browser.Version;
            string device = Request.Browser.IsMobileDevice ? "Mobile" : "Desktop";
            string os = Request.Browser.Platform;
            string ip = Request.UserHostAddress;
            string httpMethod = Request.HttpMethod;

            if (sessionUserObj != null)
            {
                RedisFeaturesService.ClearCart(sessionUserObj.UserName);

                long khachHangId = 0;
                var kh = db.Khach_Hang.FirstOrDefault(k => k.UserID == sessionUserObj.UserID);
                khachHangId = kh?.KhachHangID ?? sessionUserObj.UserID;

                // Cassandra 1: Ghi lich_su_ghe trang_thai = "CANCEL" cho mỗi ghế
                foreach (var gheId in danhSachIdGhe)
                {
                    CassandraFeaturesService.GhiLichSuGhe(new LichSuGheDTO
                    {
                        LichChieuId = lichChieuId,
                        GheId = gheId,
                        TrangThai = "CANCEL",
                        KhachHangId = khachHangId,
                        DonDatVeId = null,
                        GhiChu = "User huy giao dich truoc khi thanh toan",
                        ControllerName = "ThanhToan",
                        ActionName = "CancelTransaction",
                        RequestMethod = httpMethod,
                        Browser = browser,
                        Device = device,
                        HeDieuHanh = os,
                        IpAddress = ip,
                        KetQua = "SUCCESS"
                    });
                }

                // Cassandra 2: Ghi nhat_ky_hoat_dong CANCEL_SEAT SUCCESS
                CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
                {
                    Username = sessionUserObj.UserName,
                    HanhDong = "CANCEL_SEAT",
                    KetQua = "SUCCESS",
                    ChiTiet = "Huy giao dich - ghe: " + lockedSeatIds + " (LichChieuID=" + lichChieuId + ")",
                    ControllerName = "ThanhToan",
                    ActionName = "CancelTransaction",
                    RequestMethod = httpMethod,
                    Browser = browser,
                    Device = device,
                    HeDieuHanh = os,
                    IpAddress = ip
                });
            }

            if (!string.IsNullOrEmpty(lockedSeatIds))
                XoaKhoaGheTamThoi(lichChieuId, lockedSeatIds);

            var idPhim = LayIdPhimTuLichChieu(lichChieuId);
            return RedirectToAction("ChonSuat", "DatVe", new { idPhim });
        }

        // =====================================================================
        // POST: /ThanhToan/SendOtp
        // Log Cassandra: OTP_REQUEST SUCCESS hoặc OTP_REQUEST FAILED
        // KHÔNG log mã OTP thực tế
        // =====================================================================
        [HttpPost]
        public ActionResult SendOtp()
        {
            var sessionUser = Session["USER_SESSION"] as UserLogin;
            if (sessionUser == null)
                return Json(new { success = false, message = "Vui lòng đăng nhập!" });

            string browser = Request.Browser.Browser + " " + Request.Browser.Version;
            string device = Request.Browser.IsMobileDevice ? "Mobile" : "Desktop";
            string os = Request.Browser.Platform;
            string ip = Request.UserHostAddress;
            string httpMethod = Request.HttpMethod;

            // 1. Tạo mã OTP 6 số lưu trên Redis (TTL 120s) - KHÔNG log mã OTP
            string otpCode = RedisFeaturesService.GenerateOtp(sessionUser.UserName, 120);

            // 2. Lấy email khách hàng
            string customerEmail = null;
            string fullName = sessionUser.FullName ?? "Khách hàng";
            var khachHang = db.Khach_Hang.FirstOrDefault(kh => kh.UserID == sessionUser.UserID);
            if (khachHang != null) customerEmail = khachHang.Email;

            // 3. Thử gửi OTP qua Gmail SMTP
            var sendResult = EmailService.SendOtpViaGmail(customerEmail, otpCode, fullName);

            if (sendResult.IsSuccess)
            {
                // Cassandra: OTP_REQUEST SUCCESS (không ghi otpCode)
                CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
                {
                    Username = sessionUser.UserName,
                    HanhDong = "OTP_REQUEST",
                    KetQua = "SUCCESS",
                    ChiTiet = "Gui OTP thanh cong toi email: " + (customerEmail ?? "khong co"),
                    ControllerName = "ThanhToan",
                    ActionName = "SendOtp",
                    RequestMethod = httpMethod,
                    Browser = browser,
                    Device = device,
                    HeDieuHanh = os,
                    IpAddress = ip
                });

                return Json(new
                {
                    success = true,
                    otp = (string)null,
                    email = customerEmail,
                    ttl = 120,
                    message = $"Mã OTP 6 số đã được gửi thành công đến Gmail: {customerEmail}. Vui lòng kiểm tra hộp thư!"
                });
            }
            else
            {
                // Cassandra: OTP_REQUEST FAILED (SMTP chưa cấu hình) — vẫn log, không log OTP
                CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
                {
                    Username = sessionUser.UserName,
                    HanhDong = "OTP_REQUEST",
                    KetQua = "FAILED",
                    ChiTiet = "Gui email that bai (SMTP chua cau hinh) - email: " + (customerEmail ?? "khong co"),
                    ControllerName = "ThanhToan",
                    ActionName = "SendOtp",
                    RequestMethod = httpMethod,
                    Browser = browser,
                    Device = device,
                    HeDieuHanh = os,
                    IpAddress = ip
                });

                string displayMsg = !string.IsNullOrEmpty(customerEmail)
                    ? $"[Chế độ Demo] Đã tạo OTP trên Redis cho Gmail ({customerEmail}): {otpCode} (Hạn 120s)"
                    : $"[Chế độ Demo] Đã tạo OTP trên Redis: {otpCode} (Hạn 120s)";

                return Json(new
                {
                    success = true,
                    otp = otpCode,
                    email = customerEmail,
                    ttl = 120,
                    message = displayMsg
                });
            }
        }

        // =====================================================================
        // POST: /ThanhToan/ProcessPayment
        //
        // Thứ tự đảm bảo:
        //   1. Xác thực OTP (Redis)
        //   2. SQL Server transaction (TaoDonHangMoi + TaoChiTietVe + Commit)
        //   3. Cassandra ghi SAU khi Commit thành công:
        //      - lich_su_ghe:      mỗi ghế trang_thai = "BOOKED"
        //      - lich_su_dat_ve:   buoc = "PAYMENT_SUCCESS"
        //      - nhat_ky_hoat_dong: PAYMENT_SUCCESS SUCCESS
        //   4. Nếu SQL Rollback:
        //      - nhat_ky_hoat_dong: PAYMENT_FAILED FAILED
        // =====================================================================
        [HttpPost]
        public ActionResult ProcessPayment(long lichChieuId, string lockedSeatIds,
            string paymentMethod, string otpCode = null, bool dungDiem = false)
        {
            if (Session["USER_SESSION"] == null)
                return RedirectToAction("Index_DangNhap", "Login");

            var sessionUser = Session["USER_SESSION"] as UserLogin;
            long? maKhachHang = LayMaKhachHangTuSession();

            if (maKhachHang == null)
                return View("PaymentError", (object)"Lỗi: Không tìm thấy thông tin khách hàng.");

            string browser = Request.Browser.Browser + " " + Request.Browser.Version;
            string device = Request.Browser.IsMobileDevice ? "Mobile" : "Desktop";
            string os = Request.Browser.Platform;
            string ip = Request.UserHostAddress;
            string httpMethod = Request.HttpMethod;

            // --- Xác thực OTP ---
            if (!string.Equals(ConfigurationManager.AppSettings["EnableNoSQL"], "false", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(otpCode))
                    return View("PaymentError", (object)"Vui lòng bấm nút 'LẤY MÃ OTP' và nhập mã OTP 6 số trước khi thanh toán!");

                bool isValidOtp = RedisFeaturesService.VerifyOtp(sessionUser.UserName, otpCode.Trim());
                if (!isValidOtp)
                    return View("PaymentError", (object)"Mã OTP không chính xác hoặc đã hết hạn (120s). Vui lòng bấm 'LẤY MÃ OTP' để nhận mã mới.");
            }

            var danhSachIdGhe = TachChuoiIdGhe(lockedSeatIds);

            if (!KiemTraGheConThuocVeMinhKhong(lichChieuId, maKhachHang.Value, danhSachIdGhe))
                return View("PaymentError", (object)"Hết thời gian giữ ghế. Vui lòng chọn lại.");

            var danhSachGheCanMua = LayThongTinGhe(danhSachIdGhe);
            decimal tongTienGoc = danhSachGheCanMua.Sum(i => i.GiaTien.GetValueOrDefault());

            var khachHang = db.Khach_Hang.Find(maKhachHang.Value);
            int diemHienCo = khachHang?.DiemThanhVien ?? 0;
            decimal soTienGiam = 0;
            int diemBiTru = 0;

            // Tính giảm giá bằng điểm thành viên
            if (sessionUser.GroupID == "2" && dungDiem && diemHienCo >= 20)
            {
                decimal giaTriDiem = diemHienCo * 1000;
                if (giaTriDiem >= tongTienGoc)
                {
                    soTienGiam = tongTienGoc;
                    diemBiTru = (int)(tongTienGoc / 1000);
                }
                else
                {
                    soTienGiam = giaTriDiem;
                    diemBiTru = diemHienCo;
                }
            }

            // Áp dụng Voucher MongoDB
            string appliedVoucher = Session["APPLIED_VOUCHER_CODE"] as string;
            if (!string.IsNullOrEmpty(appliedVoucher))
            {
                var promo = doan3.Models.Mgdb.MgdbService.GetPromotionByCode(appliedVoucher);
                if (promo != null)
                {
                    soTienGiam += promo.DiscountAmount;
                    doan3.Models.Mgdb.MgdbService.UseVoucher(promo.Code, sessionUser.UserName);
                }
                Session.Remove("APPLIED_VOUCHER_CODE");
            }

            decimal tongTienPhaiTra = Math.Max(0, tongTienGoc - soTienGiam);

            // --- SQL Server Transaction ---
            using (var giaoDich = db.Database.BeginTransaction())
            {
                try
                {
                    long maDonHang = TaoDonHangMoi(maKhachHang.Value, tongTienPhaiTra);
                    TaoChiTietVe(maDonHang, lichChieuId, danhSachGheCanMua);

                    if (khachHang != null)
                    {
                        khachHang.DiemThanhVien = (khachHang.DiemThanhVien ?? 0) - diemBiTru;
                        if (sessionUser.GroupID == "2")
                            khachHang.DiemThanhVien += TinhDiemTichLuy(danhSachGheCanMua);
                    }

                    XoaKhoaGheSauKhiMuaThanhCong(lichChieuId, danhSachIdGhe);
                    RedisFeaturesService.ClearCart(sessionUser.UserName);
                    db.SaveChanges();
                    giaoDich.Commit();

                    // ===== CASSANDRA — chỉ ghi SAU KHI SQL Commit thành công =====

                    // 1. lich_su_ghe: mỗi ghế ghi trang_thai = "BOOKED"
                    foreach (var ghe in danhSachGheCanMua)
                    {
                        CassandraFeaturesService.GhiLichSuGhe(new LichSuGheDTO
                        {
                            LichChieuId = lichChieuId,
                            GheId = ghe.GheID,
                            TrangThai = "BOOKED",
                            KhachHangId = maKhachHang.Value,
                            DonDatVeId = maDonHang,
                            GhiChu = "Thanh toan OTP hop le - DonDatVeID=" + maDonHang,
                            ControllerName = "ThanhToan",
                            ActionName = "ProcessPayment",
                            RequestMethod = httpMethod,
                            Browser = browser,
                            Device = device,
                            HeDieuHanh = os,
                            IpAddress = ip,
                            KetQua = "SUCCESS"
                        });
                    }

                    // 2. lich_su_dat_ve: ghi buoc = "PAYMENT_SUCCESS"
                    CassandraFeaturesService.GhiLichSuDatVe(new LichSuDatVeDTO
                    {
                        KhachHangId = maKhachHang.Value,
                        DonDatVeId = maDonHang,
                        MaDatVe = "DV" + maDonHang.ToString("D8"),
                        Buoc = "PAYMENT_SUCCESS",
                        LichChieuId = lichChieuId,
                        SoGhe = danhSachGheCanMua.Count,
                        TongTien = tongTienPhaiTra,
                        GhiChu = "Thanh toan thanh cong - " + paymentMethod
                    });

                    // 3. nhat_ky_hoat_dong: PAYMENT_SUCCESS SUCCESS
                    CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
                    {
                        Username = sessionUser.UserName,
                        HanhDong = "PAYMENT_SUCCESS",
                        KetQua = "SUCCESS",
                        ChiTiet = "Thanh toan thanh cong - DonDatVeID=" + maDonHang
                                         + " - " + string.Format("{0:N0}", tongTienPhaiTra) + " VND",
                        ControllerName = "ThanhToan",
                        ActionName = "ProcessPayment",
                        RequestMethod = httpMethod,
                        Browser = browser,
                        Device = device,
                        HeDieuHanh = os,
                        IpAddress = ip
                    });

                    // Neo4j: ghi lượt đặt vé
                    try
                    {
                        var lc = db.Lich_Chieu.FirstOrDefault(l => l.LichChieuID == lichChieuId);
                        int targetPhimId = lc != null ? (int)(lc.PhimID ?? 0) : 0;
                        var phimObj = db.Phims.FirstOrDefault(p => p.PhimID == targetPhimId);
                        string movieTitle = phimObj != null ? phimObj.TenPhim : "";
                        var neo4jService = new Neo4jService();
                        neo4jService.AddBooking(sessionUser.UserName, targetPhimId, "BK" + maDonHang, danhSachGheCanMua.Count, tongTienPhaiTra, movieTitle);
                    }
                    catch { }

                    return RedirectToAction("PaymentSuccess", new { orderId = maDonHang });
                }
                catch (Exception loi)
                {
                    giaoDich.Rollback();

                    // Cassandra: PAYMENT_FAILED FAILED (ghi ngoài transaction đã rollback)
                    CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
                    {
                        Username = sessionUser.UserName,
                        HanhDong = "PAYMENT_FAILED",
                        KetQua = "FAILED",
                        ChiTiet = "Thanh toan that bai: " + loi.Message,
                        ControllerName = "ThanhToan",
                        ActionName = "ProcessPayment",
                        RequestMethod = httpMethod,
                        Browser = browser,
                        Device = device,
                        HeDieuHanh = os,
                        IpAddress = ip
                    });

                    return View("PaymentError", (object)("Lỗi hệ thống: " + loi.Message));
                }
            }
        }


        // =====================================================================
        // GET: /ThanhToan/PaymentSuccess
        // =====================================================================
        public ActionResult PaymentSuccess(long orderId)
        {
            var donHang = LayThongTinDonHangDayDu(orderId);
            if (donHang == null) return RedirectToAction("Index", "Home");
            return View(donHang);
        }

        // =====================================================================
        // GET/POST: /ThanhToan/PaymentError
        // =====================================================================
        public ActionResult PaymentError(string error)
        {
            ViewBag.ErrorMessage = error;
            return View();
        }

        // =====================================================================
        // GET: /ThanhToan/CheckIn?chiTietVeId=
        //
        // CHỨC NĂNG CÒN THIẾU: ghi trạng thái CHECK_IN vào Seat Reservation Timeline.
        // Tái sử dụng cột Chi_Tiet_Ve.TrangThaiSuDung (đã có sẵn trong SQL Server,
        // trước đây chỉ set = false lúc tạo vé, chưa có nơi nào set = true).
        //
        // Log Cassandra:
        //   - lich_su_ghe:        trang_thai = "CHECK_IN"
        //   - nhat_ky_hoat_dong:  CHECK_IN_TICKET SUCCESS / FAILED
        // =====================================================================
        public ActionResult CheckIn(long chiTietVeId)
        {
            if (Session["USER_SESSION"] == null)
                return RedirectToAction("Index_DangNhap", "Login");

            var sessionUser = Session["USER_SESSION"] as UserLogin;
            string browser = Request.Browser.Browser + " " + Request.Browser.Version;
            string device = Request.Browser.IsMobileDevice ? "Mobile" : "Desktop";
            string os = Request.Browser.Platform;
            string ip = Request.UserHostAddress;
            string httpMethod = Request.HttpMethod;

            var ve = db.Chi_Tiet_Ve.FirstOrDefault(v => v.ChiTietVeID == chiTietVeId);
            var donHang = ve != null ? db.Don_Dat_Ve.FirstOrDefault(d => d.DonDatVeID == ve.DonDatVeID) : null;
            long? maKhachHang = LayMaKhachHangTuSession();

            if (ve == null || donHang == null || donHang.KhachHangID != maKhachHang)
            {
                CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
                {
                    Username = sessionUser?.UserName,
                    HanhDong = "CHECK_IN_TICKET",
                    KetQua = "FAILED",
                    ChiTiet = "Khong tim thay ve hoac ve khong thuoc nguoi dung - ChiTietVeID=" + chiTietVeId,
                    ControllerName = "ThanhToan",
                    ActionName = "CheckIn",
                    RequestMethod = httpMethod,
                    Browser = browser,
                    Device = device,
                    HeDieuHanh = os,
                    IpAddress = ip
                });
                return View("PaymentError", (object)"Không tìm thấy vé hoặc vé không thuộc về bạn.");
            }

            if (ve.TrangThaiSuDung != true)
            {
                ve.TrangThaiSuDung = true;
                db.SaveChanges();
            }

            CassandraFeaturesService.GhiLichSuGhe(new LichSuGheDTO
            {
                LichChieuId = ve.LichChieuID ?? 0,
                GheId = ve.GheID ?? 0,
                TrangThai = "CHECK_IN",
                KhachHangId = maKhachHang,
                DonDatVeId = ve.DonDatVeID,
                GhiChu = "Check-in ve tai rap - ChiTietVeID=" + chiTietVeId,
                ControllerName = "ThanhToan",
                ActionName = "CheckIn",
                RequestMethod = httpMethod,
                Browser = browser,
                Device = device,
                HeDieuHanh = os,
                IpAddress = ip,
                KetQua = "SUCCESS"
            });

            CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
            {
                Username = sessionUser.UserName,
                HanhDong = "CHECK_IN_TICKET",
                KetQua = "SUCCESS",
                ChiTiet = "Check-in thanh cong - ChiTietVeID=" + chiTietVeId + " DonDatVeID=" + ve.DonDatVeID,
                ControllerName = "ThanhToan",
                ActionName = "CheckIn",
                RequestMethod = httpMethod,
                Browser = browser,
                Device = device,
                HeDieuHanh = os,
                IpAddress = ip
            });

            TempData["Message"] = "Check-in thành công cho vé #" + chiTietVeId;
            return RedirectToAction("PaymentSuccess", new { orderId = ve.DonDatVeID });
        }

        // =====================================================================
        // GET: /ThanhToan/HuyVeSauKhiDat?chiTietVeId=
        //
        // DEMO NoSQL: ghi thêm event CANCEL vào Seat Reservation Timeline SAU KHI
        // vé đã BOOKED/CHECK_IN, để chứng minh timeline lưu đầy đủ vòng đời.
        // KHÔNG đổi trạng thái đơn hàng trong SQL Server (hoàn tiền/hủy đơn thật
        // là nghiệp vụ khác, ngoài phạm vi module Cassandra).
        // =====================================================================
        public ActionResult HuyVeSauKhiDat(long chiTietVeId)
        {
            if (Session["USER_SESSION"] == null)
                return RedirectToAction("Index_DangNhap", "Login");

            var sessionUser = Session["USER_SESSION"] as UserLogin;
            string browser = Request.Browser.Browser + " " + Request.Browser.Version;
            string device = Request.Browser.IsMobileDevice ? "Mobile" : "Desktop";
            string os = Request.Browser.Platform;
            string ip = Request.UserHostAddress;
            string httpMethod = Request.HttpMethod;

            var ve = db.Chi_Tiet_Ve.FirstOrDefault(v => v.ChiTietVeID == chiTietVeId);
            var donHang = ve != null ? db.Don_Dat_Ve.FirstOrDefault(d => d.DonDatVeID == ve.DonDatVeID) : null;
            long? maKhachHang = LayMaKhachHangTuSession();

            if (ve == null || donHang == null || donHang.KhachHangID != maKhachHang)
                return View("PaymentError", (object)"Không tìm thấy vé hoặc vé không thuộc về bạn.");

            CassandraFeaturesService.GhiLichSuGhe(new LichSuGheDTO
            {
                LichChieuId = ve.LichChieuID ?? 0,
                GheId = ve.GheID ?? 0,
                TrangThai = "CANCEL",
                KhachHangId = maKhachHang,
                DonDatVeId = ve.DonDatVeID,
                GhiChu = "Huy ve sau khi da dat/check-in (demo timeline) - ChiTietVeID=" + chiTietVeId,
                ControllerName = "ThanhToan",
                ActionName = "HuyVeSauKhiDat",
                RequestMethod = httpMethod,
                Browser = browser,
                Device = device,
                HeDieuHanh = os,
                IpAddress = ip,
                KetQua = "SUCCESS"
            });

            CassandraFeaturesService.GhiNhatKyHoatDong(new NhatKyHoatDongDTO
            {
                Username = sessionUser.UserName,
                HanhDong = "CANCEL_TICKET",
                KetQua = "SUCCESS",
                ChiTiet = "Huy ve sau khi da dat - ChiTietVeID=" + chiTietVeId + " DonDatVeID=" + ve.DonDatVeID,
                ControllerName = "ThanhToan",
                ActionName = "HuyVeSauKhiDat",
                RequestMethod = httpMethod,
                Browser = browser,
                Device = device,
                HeDieuHanh = os,
                IpAddress = ip
            });

            TempData["Message"] = "Đã ghi hủy vé #" + chiTietVeId + " vào timeline.";
            return RedirectToAction("PaymentSuccess", new { orderId = ve.DonDatVeID });
        }


        // =====================================================================
        // Private helpers (giữ nguyên logic gốc)
        // =====================================================================
        private List<GheTinhDiem> LayThongTinGhe(List<long> danhSachIdGhe)
        {
            return (from g in db.Ghe_Ngoi
                    join t in db.TienVes on g.LoaiGhe equals t.LoaiGhe
                    where danhSachIdGhe.Contains(g.GheID)
                    select new GheTinhDiem
                    {
                        GheID = g.GheID,
                        MaGhe = g.MaGhe,
                        GiaTien = t.GiaTien,
                        LoaiGhe = g.LoaiGhe
                    }).ToList();
        }

        private int TinhDiemTichLuy(List<GheTinhDiem> danhSachGhe)
        {
            int tongDiem = 0;
            foreach (var ghe in danhSachGhe)
            {
                if (string.IsNullOrEmpty(ghe.LoaiGhe)) { tongDiem += 1; continue; }
                string loai = ghe.LoaiGhe.Trim().ToLower();
                if (loai.Contains("vip")) tongDiem += 2;
                else if (loai.Contains("doi") || loai.Contains("đôi") || loai.Contains("couple")) tongDiem += 3;
                else tongDiem += 1;
            }
            return tongDiem;
        }

        private int LayDiemThanhVien(long? khachHangId)
        {
            if (!khachHangId.HasValue) return 0;
            var kh = db.Khach_Hang.Find(khachHangId.Value);
            return kh != null ? (kh.DiemThanhVien ?? 0) : 0;
        }

        private void TaoChiTietVe(long maDonHang, long lichChieuId, List<GheTinhDiem> danhSachGhe)
        {
            foreach (var ghe in danhSachGhe)
            {
                db.Chi_Tiet_Ve.Add(new Chi_Tiet_Ve
                {
                    DonDatVeID = maDonHang,
                    LichChieuID = lichChieuId,
                    GheID = ghe.GheID,
                    LoaiGhe = ghe.LoaiGhe,
                    LoaiVe = "Nguoi Lon",
                    TrangThaiSuDung = false
                });
            }
        }

        private long? LayMaKhachHangTuSession()
        {
            var sessionNguoiDung = Session["USER_SESSION"] as UserLogin;
            if (sessionNguoiDung == null || sessionNguoiDung.UserID == 0) return null;
            var kh = db.Khach_Hang.FirstOrDefault(k => k.UserID == sessionNguoiDung.UserID);
            return kh?.KhachHangID;
        }

        private Lich_Chieu LayThongTinLichChieu(long lichChieuId)
        {
            return db.Lich_Chieu
                .Include(l => l.Phim)
                .Include(l => l.Phong_Chieu.Rap_Chieu)
                .FirstOrDefault(l => l.LichChieuID == lichChieuId);
        }

        private List<long> TachChuoiIdGhe(string chuoiIdGhe)
        {
            if (string.IsNullOrEmpty(chuoiIdGhe)) return new List<long>();
            return chuoiIdGhe.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(id => long.Parse(id.Trim())).ToList();
        }

        private long TinhThoiGianGiuGheConLai(long lichChieuId, List<long> danhSachIdGhe)
        {
            return SeatLockService.GetRemainingLockTime(lichChieuId, danhSachIdGhe);
        }

        private void XoaKhoaGheTamThoi(long lichChieuId, string chuoiIdGhe)
        {
            SeatLockService.ReleaseSeatLocks(lichChieuId, TachChuoiIdGhe(chuoiIdGhe));
        }

        private long LayIdPhimTuLichChieu(long lichChieuId)
        {
            var lc = db.Lich_Chieu.FirstOrDefault(l => l.LichChieuID == lichChieuId);
            return lc?.PhimID ?? 1;
        }

        private bool KiemTraGheConThuocVeMinhKhong(long lichChieuId, long maKhachHang, List<long> danhSachIdGhe)
        {
            return SeatLockService.VerifySeatsLockedByCustomer(lichChieuId, danhSachIdGhe, maKhachHang);
        }

        private long TaoDonHangMoi(long maKhachHang, decimal tongTien)
        {
            var donHang = new Don_Dat_Ve
            {
                KhachHangID = maKhachHang,
                TongTienDonHang = tongTien,
                TrangThaiDonHang = "Đã thanh toán",
                ThoiGianDat = DateTime.Now
            };
            db.Don_Dat_Ve.Add(donHang);
            db.SaveChanges();
            return donHang.DonDatVeID;
        }

        private void XoaKhoaGheSauKhiMuaThanhCong(long lichChieuId, List<long> danhSachIdGhe)
        {
            SeatLockService.ReleaseSeatLocks(lichChieuId, danhSachIdGhe);
        }

        private Don_Dat_Ve LayThongTinDonHangDayDu(long maDonHang)
        {
            return db.Don_Dat_Ve
                .Include("Khach_Hang")
                .Include("Chi_Tiet_Ve")
                .Include("Chi_Tiet_Ve.Lich_Chieu")
                .Include("Chi_Tiet_Ve.Lich_Chieu.Phim")
                .Include("Chi_Tiet_Ve.Ghe_Ngoi")
                .FirstOrDefault(d => d.DonDatVeID == maDonHang);
        }
    }
}
