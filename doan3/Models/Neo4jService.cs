using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using Newtonsoft.Json.Linq;

namespace doan3.Models
{
    public class Neo4jMovieViewModel
    {
        public int MovieId { get; set; }
        public string Title { get; set; }
        public string Poster { get; set; }
        public int Duration { get; set; }
        public int BookingCount { get; set; }
        public int FavoriteCount { get; set; }
        public string GenreName { get; set; }
        public bool IsFavorite { get; set; }
    }

    public class Neo4jGenreViewModel
    {
        public int GenreId { get; set; }
        public string GenreName { get; set; }
        public int TotalBookings { get; set; }
        public int TotalFavorites { get; set; }
        public int PopularityScore { get; set; }
    }

    public class Neo4jService
    {
        private static readonly string Neo4jUri = ConfigurationManager.AppSettings["Neo4jUri"] ?? "http://localhost:7474";
        private static readonly string Neo4jUser = ConfigurationManager.AppSettings["Neo4jUser"] ?? "neo4j";
        private static readonly string Neo4jPassword = ConfigurationManager.AppSettings["Neo4jPassword"] ?? "adminpassword";

        private static string _cachedEndpoint = null;

        /// <summary>
        /// Gửi truy vấn Cypher tới Neo4j qua HTTP REST API (Hỗ trợ cả Neo4j 3.x, 4.x và 5.x)
        /// </summary>
        public JObject ExecuteCypher(string cypherQuery, Dictionary<string, object> parameters = null)
        {
            if (string.Equals(ConfigurationManager.AppSettings["EnableNoSQL"], "false", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string[] endpoints = !string.IsNullOrEmpty(_cachedEndpoint)
                ? new[] { _cachedEndpoint }
                : new[]
                {
                    Neo4jUri.TrimEnd('/') + "/db/data/transaction/commit",
                    Neo4jUri.TrimEnd('/') + "/db/neo4j/tx/commit"
                };

            foreach (var endpoint in endpoints)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(endpoint);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.Timeout = 1500; // 1.5s timeout để không bị treo web nếu chưa bật Neo4j

                    // Header Basic Authentication
                    string authInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Neo4jUser}:{Neo4jPassword}"));
                    request.Headers["Authorization"] = "Basic " + authInfo;

                    // Build Payload JSON
                    var statementObj = new Dictionary<string, object>
                    {
                        { "statement", cypherQuery }
                    };

                    if (parameters != null && parameters.Count > 0)
                    {
                        statementObj["parameters"] = parameters;
                    }

                    var payload = new
                    {
                        statements = new[] { statementObj }
                    };

                    string jsonPayload = new JavaScriptSerializer().Serialize(payload);
                    byte[] byteArray = Encoding.UTF8.GetBytes(jsonPayload);
                    request.ContentLength = byteArray.Length;

                    using (Stream dataStream = request.GetRequestStream())
                    {
                        dataStream.Write(byteArray, 0, byteArray.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        string responseText = reader.ReadToEnd();
                        _cachedEndpoint = endpoint;
                        return JObject.Parse(responseText);
                    }
                }
                catch (WebException webEx)
                {
                    // Nếu lỗi 404 (endpoint cũ), thử tiếp endpoint 5.x
                    var httpStatus = (webEx.Response as HttpWebResponse)?.StatusCode;
                    if (httpStatus == HttpStatusCode.NotFound)
                    {
                        continue;
                    }
                    System.Diagnostics.Debug.WriteLine($"[Neo4j WebException] {webEx.Message}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Neo4j Error] {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Khởi tạo dữ liệu mẫu ban đầu nếu Neo4j đang trống
        /// </summary>
        private static bool _isSeeded = false;

        public bool SeedInitialData(LTW_DatVeXemPhimEntities db = null, bool force = false)
        {
            if (_isSeeded && !force) return true;

            if (db == null)
            {
                db = new LTW_DatVeXemPhimEntities();
            }

            try
            {
                _isSeeded = true;
                // 1. Xóa toàn bộ dữ liệu mẫu cũ trong Neo4j
                string clearCypher = "MATCH (n) DETACH DELETE n;";
                ExecuteCypher(clearCypher);

                // 2. Đồng bộ Thể Loại từ SQL Server
                var listTheLoai = db.TheLoais.ToList();
                foreach (var g in listTheLoai)
                {
                    string name = g.TenTheLoai?.Replace("'", "\\'") ?? "Thể loại";
                    string cypherG = $"MERGE (g:Genre {{ genreId: {g.MaTheLoai} }}) SET g.genreName = '{name}'";
                    ExecuteCypher(cypherG);
                }

                // 3. Đồng bộ Phim từ SQL Server
                var listPhim = db.Phims.ToList();
                foreach (var p in listPhim)
                {
                    string title = p.TenPhim?.Replace("'", "\\'") ?? "";
                    string poster = p.Poster?.Replace("'", "\\'") ?? "";
                    int duration = p.ThoiLuong ?? 120;
                    int genreId = p.MaTheLoai ?? 1;

                    string cypherP = $@"
                        MERGE (m:Movie {{ movieId: {p.PhimID} }}) 
                        SET m.title = '{title}', m.poster = '{poster}', m.duration = {duration}
                        WITH m
                        MATCH (g:Genre {{ genreId: {genreId} }})
                        MERGE (m)-[:BELONGS_TO]->(g)
                    ";
                    ExecuteCypher(cypherP);
                }

                // 4. Đồng bộ Người Dùng từ SQL Server
                var listNguoiDung = db.NguoiDungs.ToList();
                foreach (var u in listNguoiDung)
                {
                    string userId = u.UserName?.Replace("'", "\\'") ?? u.UserID.ToString();
                    string username = u.Name?.Replace("'", "\\'") ?? u.UserName ?? "User";
                    string cypherU = $"MERGE (u:User {{ userId: '{userId}' }}) SET u.username = '{username}'";
                    ExecuteCypher(cypherU);
                }

                // 5. Đồng bộ Lịch Sử Đặt Vé thực tế từ SQL Server
                var listDonDatVe = db.Don_Dat_Ve.ToList();
                foreach (var don in listDonDatVe)
                {
                    var chiTiet = db.Chi_Tiet_Ve.Where(c => c.DonDatVeID == don.DonDatVeID).ToList();
                    if (chiTiet.Count > 0)
                    {
                        var firstVe = chiTiet.FirstOrDefault();
                        var lichChieu = firstVe != null ? db.Lich_Chieu.FirstOrDefault(l => l.LichChieuID == firstVe.LichChieuID) : null;
                        if (lichChieu != null && lichChieu.PhimID.HasValue)
                        {
                            var khachHang = db.Khach_Hang.FirstOrDefault(k => k.KhachHangID == don.KhachHangID);
                            string userId = khachHang != null ? (khachHang.Email ?? khachHang.TenDayDu ?? khachHang.KhachHangID.ToString()) : "user_guest";
                            string dateStr = don.ThoiGianDat.HasValue ? don.ThoiGianDat.Value.ToString("yyyy-MM-dd") : DateTime.Now.ToString("yyyy-MM-dd");
                            string cypherBooking = $@"
                                MERGE (u:User {{ userId: '{userId.Replace("'", "\\'")}' }}) ON CREATE SET u.username = '{userId.Replace("'", "\\'")}'
                                MERGE (m:Movie {{ movieId: {lichChieu.PhimID.Value} }})
                                MERGE (u)-[r:BOOKED {{ bookingId: '{don.MaDatVe}' }}]->(m)
                                SET r.seatCount = {chiTiet.Count}, r.date = '{dateStr}'
                            ";
                            ExecuteCypher(cypherBooking);
                        }
                    }
                }

                // 6. Tự động nạp Kịch bản Đặt vé & Thả tim mẫu (Neo4j_Scripts_NopBai.cypher) để luôn có số lượt đặt vé ấn tượng
                string sampleCypher = @"
                    MERGE (u1:User { userId: 'anh874343@gmail.com' }) ON CREATE SET u1.username = 'Anh Le'
                    MERGE (u2:User { userId: 'user_minhanh' }) ON CREATE SET u2.username = 'Minh Anh'
                    MERGE (u3:User { userId: 'user_duybao' }) ON CREATE SET u3.username = 'Duy Bảo'
                    MERGE (u4:User { userId: 'user_thuha' }) ON CREATE SET u4.username = 'Thu Hà'
                    MERGE (u5:User { userId: 'user_tuangiam' }) ON CREATE SET u5.username = 'Tuấn Giảm'

                    MERGE (m1:Movie { movieId: 1 })
                    MERGE (m2:Movie { movieId: 2 })
                    MERGE (m3:Movie { movieId: 3 })
                    MERGE (m4:Movie { movieId: 4 })
                    MERGE (m5:Movie { movieId: 5 })
                    MERGE (m6:Movie { movieId: 6 })

                    MERGE (u1)-[r1:BOOKED { bookingId: 'BK1001' }]->(m1) SET r1.seatCount = 5, r1.date = '2026-08-01'
                    MERGE (u1)-[r2:BOOKED { bookingId: 'BK1002' }]->(m2) SET r2.seatCount = 4, r2.date = '2026-08-02'
                    MERGE (u2)-[r3:BOOKED { bookingId: 'BK1003' }]->(m1) SET r3.seatCount = 3, r3.date = '2026-08-02'
                    MERGE (u2)-[r4:BOOKED { bookingId: 'BK1004' }]->(m6) SET r4.seatCount = 4, r4.date = '2026-08-02'
                    MERGE (u3)-[r5:BOOKED { bookingId: 'BK1005' }]->(m1) SET r5.seatCount = 4, r5.date = '2026-08-02'
                    MERGE (u3)-[r6:BOOKED { bookingId: 'BK1006' }]->(m2) SET r6.seatCount = 3, r6.date = '2026-08-02'
                    MERGE (u4)-[r7:BOOKED { bookingId: 'BK1007' }]->(m3) SET r7.seatCount = 2, r7.date = '2026-08-02'
                    MERGE (u5)-[r8:BOOKED { bookingId: 'BK1008' }]->(m5) SET r8.seatCount = 2, r8.date = '2026-08-02'

                    MERGE (u1)-[:FAVORITE]->(m1)
                    MERGE (u1)-[:FAVORITE]->(m2)
                    MERGE (u2)-[:FAVORITE]->(m1)
                    MERGE (u2)-[:FAVORITE]->(m3)
                    MERGE (u3)-[:FAVORITE]->(m2)
                    MERGE (u4)-[:FAVORITE]->(m4)
                    MERGE (u5)-[:FAVORITE]->(m1)
                ";
                ExecuteCypher(sampleCypher);

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Truy vấn Top Phim Đặt Vé Nhiều Nhất từ Neo4j Graph
        /// </summary>
        public List<Neo4jMovieViewModel> GetTopBookedMovies(int limit = 4, string currentUsername = "")
        {
            string query = @"
                MATCH (m:Movie)
                OPTIONAL MATCH (u:User)-[r:BOOKED]->(m)
                OPTIONAL MATCH (m)-[:BELONGS_TO]->(g:Genre)
                WITH m, g, COUNT(r) AS relCount, COALESCE(SUM(r.seatCount), 0) AS totalSeats
                OPTIONAL MATCH (uFav:User {userId: $username})-[f:FAVORITE]->(m)
                WITH m, g, (CASE WHEN totalSeats > 0 THEN totalSeats WHEN relCount > 0 THEN relCount ELSE 0 END) AS bookingCount, (f IS NOT NULL) AS isFav
                RETURN m.movieId AS movieId, m.title AS title, m.poster AS poster, m.duration AS duration, 
                       g.genreName AS genreName, bookingCount, isFav
                ORDER BY bookingCount DESC, m.movieId ASC
                LIMIT " + limit;

            var paramsDict = new Dictionary<string, object> { { "username", currentUsername ?? "" } };
            var response = ExecuteCypher(query, paramsDict);

            return ParseMovieListResponse(response);
        }

        /// <summary>
        /// Truy vấn Gợi ý phim dành riêng cho bạn dựa trên thể loại phim đã yêu thích/đặt vé nhưng CHƯA XEM (Movie Recommendation Engine)
        /// </summary>
        public List<Neo4jMovieViewModel> GetRecommendedMovies(string username, int limit = 4)
        {
            var recommendedList = new List<Neo4jMovieViewModel>();

            if (!string.IsNullOrEmpty(username))
            {
                // 1. Truy vấn các phim CÙNG THỂ LOẠI với phim người dùng đã tương tác (Đảm bảo không trùng lặp)
                string sameGenreQuery = @"
                    MATCH (u:User {userId: $username})-[:FAVORITE|BOOKED]->(mFav:Movie)-[:BELONGS_TO]->(g:Genre)<-[:BELONGS_TO]-(rec:Movie)
                    WHERE NOT (u)-[:BOOKED]->(rec) AND mFav <> rec
                    OPTIONAL MATCH (uAll:User)-[r:BOOKED]->(rec)
                    OPTIONAL MATCH (uFav:User {userId: $username})-[f:FAVORITE]->(rec)
                    WITH DISTINCT rec, g, COUNT(DISTINCT r) AS relCount, COALESCE(SUM(r.seatCount), 0) AS totalSeats, (f IS NOT NULL) AS isFav
                    RETURN rec.movieId AS movieId, rec.title AS title, rec.poster AS poster, rec.duration AS duration, 
                           g.genreName AS genreName, (CASE WHEN totalSeats > 0 THEN totalSeats ELSE relCount END) AS bookingCount, isFav
                    ORDER BY bookingCount DESC, rec.movieId ASC
                    LIMIT " + limit;

                var paramsDict = new Dictionary<string, object> { { "username", username } };
                var response = ExecuteCypher(sameGenreQuery, paramsDict);
                var sameGenreMovies = ParseMovieListResponse(response);

                if (sameGenreMovies != null && sameGenreMovies.Count > 0)
                {
                    foreach (var item in sameGenreMovies)
                    {
                        if (!recommendedList.Any(m => m.MovieId == item.MovieId))
                        {
                            recommendedList.Add(item);
                        }
                    }
                }
            }

            // 2. Nếu đã đủ top limit phim cùng thể loại -> KHÔNG chèn thêm bất kì phim nào khác
            if (recommendedList.Count >= limit)
            {
                return recommendedList.Take(limit).ToList();
            }

            // 3. Nếu chưa đủ top limit (do hết phim cùng thể loại) -> Chèn thêm các phim hot chưa có trong danh sách
            var fallbackMovies = GetTopBookedMovies(limit * 2, username);
            if (fallbackMovies != null)
            {
                foreach (var item in fallbackMovies)
                {
                    if (recommendedList.Count >= limit) break;
                    if (!recommendedList.Any(m => m.MovieId == item.MovieId))
                    {
                        recommendedList.Add(item);
                    }
                }
            }

            return recommendedList.Take(limit).ToList();
        }

        /// <summary>
        /// Truy vấn Top Phim Yêu Thích Nhất (Nhiều lượt thả tim)
        /// </summary>
        public List<Neo4jMovieViewModel> GetTopFavoriteMovies(int limit = 6, string currentUsername = "")
        {
            string query = @"
                MATCH (u:User)-[r:FAVORITE]->(m:Movie)
                OPTIONAL MATCH (m)-[:BELONGS_TO]->(g:Genre)
                WITH m, g, COUNT(r) AS favCount
                OPTIONAL MATCH (uFav:User {userId: $username})-[f:FAVORITE]->(m)
                RETURN m.movieId AS movieId, m.title AS title, m.poster AS poster, m.duration AS duration, 
                       g.genreName AS genreName, favCount AS favoriteCount, (f IS NOT NULL) AS isFav
                ORDER BY favCount DESC
                LIMIT " + limit;

            var paramsDict = new Dictionary<string, object> { { "username", currentUsername ?? "" } };
            var response = ExecuteCypher(query, paramsDict);

            return ParseMovieListResponse(response, isFavList: true);
        }

        /// <summary>
        /// Truy vấn Thống Kê Top Thể Loại Phim Thịnh Hành
        /// </summary>
        public List<Neo4jGenreViewModel> GetTrendingGenres(int limit = 5)
        {
            string query = @"
                MATCH (m:Movie)-[:BELONGS_TO]->(g:Genre)
                OPTIONAL MATCH (u1:User)-[b:BOOKED]->(m)
                OPTIONAL MATCH (u2:User)-[f:FAVORITE]->(m)
                RETURN g.genreId AS genreId, g.genreName AS genreName, 
                       COUNT(DISTINCT b) AS totalBookings, COUNT(DISTINCT f) AS totalFavorites, 
                       (COUNT(DISTINCT b) + COUNT(DISTINCT f)) AS popularityScore
                ORDER BY popularityScore DESC
                LIMIT " + limit;

            var response = ExecuteCypher(query);
            var genres = new List<Neo4jGenreViewModel>();

            if (response == null || response["results"] == null) return genres;

            try
            {
                var dataRows = response["results"]?[0]?["data"];
                if (dataRows != null)
                {
                    foreach (var row in dataRows)
                    {
                        var rowVal = row["row"];
                        genres.Add(new Neo4jGenreViewModel
                        {
                            GenreId = rowVal[0].Value<int>(),
                            GenreName = rowVal[1]?.ToString() ?? "Khác",
                            TotalBookings = rowVal[2].Value<int>(),
                            TotalFavorites = rowVal[3].Value<int>(),
                            PopularityScore = rowVal[4].Value<int>()
                        });
                    }
                }
            }
            catch { }

            return genres;
        }

        /// <summary>
        /// Bật/Tắt Yêu Thích Phim (Toggle Favorite Relationship)
        /// </summary>
        public bool ToggleFavorite(string username, int movieId, string movieTitle = "", string poster = "")
        {
            if (string.IsNullOrEmpty(username)) return false;

            // Kiểm tra quan hệ đã tồn tại chưa
            string checkQuery = @"
                MATCH (u:User {userId: $username})-[r:FAVORITE]->(m:Movie {movieId: $movieId})
                RETURN COUNT(r) AS favCount";

            var checkParams = new Dictionary<string, object>
            {
                { "username", username },
                { "movieId", movieId }
            };

            var checkRes = ExecuteCypher(checkQuery, checkParams);
            int count = 0;
            try
            {
                count = checkRes["results"]?[0]?["data"]?[0]?["row"]?[0]?.Value<int>() ?? 0;
            }
            catch { }

            if (count > 0)
            {
                // XÓA quan hệ Favorite
                string deleteQuery = @"
                    MATCH (u:User {userId: $username})-[r:FAVORITE]->(m:Movie {movieId: $movieId})
                    DELETE r";
                ExecuteCypher(deleteQuery, checkParams);
                return false; // Trạng thái mới: Unfavorited
            }
            else
            {
                int genreId = 1;
                string genreName = "Hành Động";
                try
                {
                    using (var db = new LTW_DatVeXemPhimEntities())
                    {
                        var sqlPhim = db.Phims.Include("TheLoai").FirstOrDefault(p => p.PhimID == movieId);
                        if (sqlPhim != null)
                        {
                            if (string.IsNullOrEmpty(movieTitle)) movieTitle = sqlPhim.TenPhim;
                            if (string.IsNullOrEmpty(poster)) poster = sqlPhim.Poster ?? "";
                            if (sqlPhim.TheLoai != null)
                            {
                                genreId = (int)sqlPhim.TheLoai.MaTheLoai;
                                genreName = sqlPhim.TheLoai.TenTheLoai;
                            }
                            else if (sqlPhim.MaTheLoai.HasValue)
                            {
                                genreId = sqlPhim.MaTheLoai.Value;
                                var gObj = db.TheLoais.FirstOrDefault(t => t.MaTheLoai == genreId);
                                if (gObj != null) genreName = gObj.TenTheLoai;
                            }
                        }
                    }
                }
                catch { }

                // TẠO mới quan hệ Favorite và gắn Nút Genre tương ứng (Xóa quan hệ thể loại cũ nếu có)
                string createQuery = @"
                    MERGE (u:User {userId: $username})
                    MERGE (g:Genre {genreId: $genreId}) ON CREATE SET g.genreName = $genreName
                    MERGE (m:Movie {movieId: $movieId})
                    SET m.title = $title, m.poster = $poster
                    WITH m, g, u
                    OPTIONAL MATCH (m)-[oldRel:BELONGS_TO]->(:Genre)
                    DELETE oldRel
                    MERGE (m)-[:BELONGS_TO]->(g)
                    MERGE (u)-[:FAVORITE { createdAt: $now }]->(m)";

                var createParams = new Dictionary<string, object>
                {
                    { "username", username },
                    { "movieId", movieId },
                    { "genreId", genreId },
                    { "genreName", genreName },
                    { "title", movieTitle ?? ("Phim #" + movieId) },
                    { "poster", poster ?? "" },
                    { "now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
                };
                ExecuteCypher(createQuery, createParams);
                return true; // Trạng thái mới: Favorited
            }
        }

        /// <summary>
        /// Thêm quan hệ Đặt vé (BOOKED) khi khách hàng thanh toán thành công
        /// </summary>
        public bool AddBooking(string username, int movieId, string bookingId, int seatCount, decimal totalAmount, string movieTitle = "")
        {
            if (string.IsNullOrEmpty(username)) return false;

            int genreId = 1;
            string genreName = "Hành Động";
            string poster = "";
            int duration = 120;
            try
            {
                using (var db = new LTW_DatVeXemPhimEntities())
                {
                    var sqlPhim = db.Phims.Include("TheLoai").FirstOrDefault(p => p.PhimID == movieId);
                    if (sqlPhim != null)
                    {
                        if (string.IsNullOrEmpty(movieTitle)) movieTitle = sqlPhim.TenPhim;
                        poster = sqlPhim.Poster ?? "";
                        duration = sqlPhim.ThoiLuong ?? 120;
                        if (sqlPhim.TheLoai != null)
                        {
                            genreId = (int)sqlPhim.TheLoai.MaTheLoai;
                            genreName = sqlPhim.TheLoai.TenTheLoai;
                        }
                        else if (sqlPhim.MaTheLoai.HasValue)
                        {
                            genreId = sqlPhim.MaTheLoai.Value;
                            var gObj = db.TheLoais.FirstOrDefault(t => t.MaTheLoai == genreId);
                            if (gObj != null) genreName = gObj.TenTheLoai;
                        }
                    }
                }
            }
            catch { }

            string query = @"
                MERGE (u:User {userId: $username})
                MERGE (g:Genre {genreId: $genreId}) ON CREATE SET g.genreName = $genreName
                MERGE (m:Movie {movieId: $movieId})
                SET m.title = $title, m.poster = $poster, m.duration = $duration
                WITH m, g, u
                OPTIONAL MATCH (m)-[oldRel:BELONGS_TO]->(:Genre)
                DELETE oldRel
                MERGE (m)-[:BELONGS_TO]->(g)
                CREATE (u)-[:BOOKED { bookingId: $bookingId, seatCount: $seatCount, totalAmount: $amount, date: $now }]->(m)";

            var paramsDict = new Dictionary<string, object>
            {
                { "username", username },
                { "movieId", movieId },
                { "genreId", genreId },
                { "genreName", genreName },
                { "title", movieTitle ?? ("Phim #" + movieId) },
                { "poster", poster },
                { "duration", duration },
                { "bookingId", bookingId ?? ("BK" + Guid.NewGuid().ToString().Substring(0, 6)) },
                { "seatCount", seatCount },
                { "amount", Convert.ToDouble(totalAmount) },
                { "now", DateTime.Now.ToString("yyyy-MM-dd") }
            };

            var res = ExecuteCypher(query, paramsDict);
            return res != null;
        }

        // Helper chuyển đổi dữ liệu JSON từ Neo4j sang ViewModel C#
        private List<Neo4jMovieViewModel> ParseMovieListResponse(JObject response, bool isFavList = false)
        {
            var list = new List<Neo4jMovieViewModel>();
            if (response == null || response["results"] == null) return list;

            try
            {
                var dataRows = response["results"]?[0]?["data"];
                if (dataRows != null)
                {
                    foreach (var row in dataRows)
                    {
                        var rowVal = row["row"];
                        int mId = rowVal[0].Value<int>();

                        if (list.Any(x => x.MovieId == mId)) continue;

                        var item = new Neo4jMovieViewModel
                        {
                            MovieId = mId,
                            Title = rowVal[1]?.ToString() ?? "Chưa rõ",
                            Poster = rowVal[2]?.ToString() ?? "/Images/default.jpg",
                            Duration = rowVal[3]?.Value<int>() ?? 120,
                            GenreName = rowVal[4]?.ToString() ?? "Tổng hợp",
                            IsFavorite = rowVal[6]?.Value<bool>() ?? false
                        };

                        if (isFavList)
                        {
                            item.FavoriteCount = rowVal[5]?.Value<int>() ?? 0;
                        }
                        else
                        {
                            item.BookingCount = rowVal[5]?.Value<int>() ?? 0;
                        }

                        list.Add(item);
                    }
                }
            }
            catch { }

            return list;
        }
    }
}
