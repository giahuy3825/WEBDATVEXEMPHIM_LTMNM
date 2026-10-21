-- =========================================================================================
-- FILE SQL: RESET DỮ LIỆU ĐỂ DEMO TỪ ĐẦU (CLEAN DEMO DATA)
-- Database: LTW_DatVeXemPhim
-- =========================================================================================
-- MỤC ĐÍCH:
--   1. Xóa sạch dữ liệu giao dịch: Đơn đặt vé, chi tiết vé, ghế đang khóa tạm, thanh toán.
--   2. Xóa sạch danh sách Khách hàng đã đăng ký (để bạn demo chức năng Đăng ký mới từ đầu).
--   3. Xóa các tài khoản người dùng cũ - CHỈ GIỮ LẠI DUY NHẤT 1 TÀI KHOẢN ADMIN:
--        * Username: admin
--        * Password: admin123
--        * Role: Admin
--   4. GIỮ NGUYÊN DỮ LIỆU NỀN TẢNG (Phim, Rạp, Phòng chiếu, Ghế ngồi, Thể loại, Bảng giá vé)
--      để hệ thống hiển thị đầy đủ, không bị lỗi thiếu dữ liệu nền.
-- =========================================================================================

USE LTW_DatVeXemPhim;
GO

SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

PRINT N'===> BẮT ĐẦU QUÁ TRÌNH RESET DỮ LIỆU DEMO...';

BEGIN TRANSACTION;
BEGIN TRY

    -----------------------------------------------------------------------------------------
    -- BƯỚC 1: XÓA CÁC BẢNG CHI TIẾT GIAO DỊCH CON (TRÁNH LỖI KHÓA NGOẠI FOREIGN KEY)
    -----------------------------------------------------------------------------------------
    PRINT N'1. Đang xóa Chi tiết vé & Ghế tạm...';
    DELETE FROM Chi_Tiet_Ve;
    DELETE FROM Khoa_Ghe_Tam_Thoi;

    -- Xóa bảng lịch sử thanh toán nếu bảng tồn tại
    IF OBJECT_ID('dbo.Lich_Su_Thanh_Toan', 'U') IS NOT NULL
    BEGIN
        DELETE FROM Lich_Su_Thanh_Toan;
    END;

    -----------------------------------------------------------------------------------------
    -- BƯỚC 2: XÓA ĐƠN ĐẶT VÉ
    -----------------------------------------------------------------------------------------
    PRINT N'2. Đang xóa Đơn đặt vé...';
    DELETE FROM Don_Dat_Ve;

    -----------------------------------------------------------------------------------------
    -- BƯỚC 3: XÓA NHẬT KÝ HOẠT ĐỘNG (LOG)
    -----------------------------------------------------------------------------------------
    PRINT N'3. Đang xóa Nhật ký hoạt động cũ...';
    IF OBJECT_ID('dbo.NhatKyHoatDong', 'U') IS NOT NULL
    BEGIN
        DELETE FROM NhatKyHoatDong;
    END;

    -----------------------------------------------------------------------------------------
    -- BƯỚC 4: XÓA TOÀN BỘ KHÁCH HÀNG (ĐỂ DEMO ĐĂNG KÝ MỚI)
    -----------------------------------------------------------------------------------------
    PRINT N'4. Đang xóa toàn bộ thông tin Khách hàng...';
    DELETE FROM Khach_Hang;

    -----------------------------------------------------------------------------------------
    -- BƯỚC 5: XÓA TOÀN BỘ NGƯỜI DÙNG - CHỈ GIỮ LẠI TÀI KHOẢN ADMIN
    -----------------------------------------------------------------------------------------
    PRINT N'5. Đang xóa tài khoản người dùng cũ (chỉ giữ lại admin)...';
    DELETE FROM NguoiDung WHERE UserName <> 'admin';

    -- Đảm bảo tài khoản admin tồn tại với thông tin chuẩn
    IF NOT EXISTS (SELECT 1 FROM NguoiDung WHERE UserName = 'admin')
    BEGIN
        PRINT N'   -> Tạo lại tài khoản admin mặc định...';
        INSERT INTO NguoiDung (UserName, Password, GroupID, Name)
        VALUES ('admin', 'admin123', 1, N'Quản Trị Viên');
    END
    ELSE
    BEGIN
        PRINT N'   -> Cập nhật lại mật khẩu admin chuẩn (admin / admin123)...';
        UPDATE NguoiDung 
        SET Password = 'admin123', GroupID = 1, Name = N'Quản Trị Viên'
        WHERE UserName = 'admin';
    END;

    -----------------------------------------------------------------------------------------
    -- BƯỚC 6: RESET LẠI IDENTITY (SỐ THỨ TỰ TỰ TĂNG) CHO CÁC BẢNG ĐÃ XÓA
    -----------------------------------------------------------------------------------------
    PRINT N'6. Đang reset số thứ tự tự tăng (IDENTITY) về 0...';
    DBCC CHECKIDENT ('Chi_Tiet_Ve', RESEED, 0);
    DBCC CHECKIDENT ('Khoa_Ghe_Tam_Thoi', RESEED, 0);
    DBCC CHECKIDENT ('Don_Dat_Ve', RESEED, 0);
    DBCC CHECKIDENT ('Khach_Hang', RESEED, 0);

    IF OBJECT_ID('dbo.Lich_Su_Thanh_Toan', 'U') IS NOT NULL
    BEGIN
        DBCC CHECKIDENT ('Lich_Su_Thanh_Toan', RESEED, 0);
    END;

    IF OBJECT_ID('dbo.NhatKyHoatDong', 'U') IS NOT NULL
    BEGIN
        DBCC CHECKIDENT ('NhatKyHoatDong', RESEED, 0);
    END;

    -----------------------------------------------------------------------------------------
    -- BƯỚC 7 (KHUYẾN NGHỊ CHO DEMO): DỜI LỊCH CHIẾU VỀ HÔM NAY VÀ NHỮNG NGÀY TỚI
    -- Giúp trang web luôn có suất chiếu hợp lệ trong tương lai để bạn bấm đặt vé thành công.
    -----------------------------------------------------------------------------------------
    PRINT N'7. Đang cập nhật ngày chiếu của các suất chiếu sang tuần hiện tại để sẵn sàng demo đặt vé...';
    DECLARE @MinDate DATE;
    SELECT @MinDate = CAST(MIN(ThoiGianBatDau) AS DATE) FROM Lich_Chieu;

    IF @MinDate IS NOT NULL
    BEGIN
        DECLARE @DayDiff INT = DATEDIFF(DAY, @MinDate, CAST(GETDATE() AS DATE));
        -- Dời ngày chiếu tiến tới ngày hôm nay
        UPDATE Lich_Chieu
        SET ThoiGianBatDau = DATEADD(DAY, @DayDiff, ThoiGianBatDau);
    END;

    COMMIT TRANSACTION;
    PRINT N'===> RESET DỮ LIỆU DEMO THÀNH CÔNG RỰC RỠ!';

END TRY
BEGIN CATCH
    ROLLBACK TRANSACTION;
    PRINT N'LỖI XẢY RA TRONG QUÁ TRÌNH RESET: ' + ERROR_MESSAGE();
END CATCH;
GO

-----------------------------------------------------------------------------------------
-- BƯỚC 8: KIỂM TRA LẠI DỮ LIỆU SAU KHI RESET
-----------------------------------------------------------------------------------------
SELECT 'NguoiDung (Con lai)' AS Bang, COUNT(*) AS SoLuong FROM NguoiDung
UNION ALL
SELECT 'Khach_Hang', COUNT(*) FROM Khach_Hang
UNION ALL
SELECT 'Don_Dat_Ve', COUNT(*) FROM Don_Dat_Ve
UNION ALL
SELECT 'Chi_Tiet_Ve', COUNT(*) FROM Chi_Tiet_Ve
UNION ALL
SELECT 'Phim (Giu nguyen)', COUNT(*) FROM Phim
UNION ALL
SELECT 'Rap_Chieu (Giu nguyen)', COUNT(*) FROM Rap_Chieu
UNION ALL
SELECT 'Ghe_Ngoi (Giu nguyen)', COUNT(*) FROM Ghe_Ngoi
UNION ALL
SELECT 'Lich_Chieu (Giu nguyen)', COUNT(*) FROM Lich_Chieu;

-- In ra thông tin tài khoản admin duy nhất còn lại
SELECT UserID, UserName, Password, Name, GroupID FROM NguoiDung;
GO
