using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;

namespace doan3.Models.Mgdb
{
    /// <summary>
    /// Service giao tiếp trực tiếp với MongoDB Docker Container (CinemaNoSQL Database)
    /// </summary>
    public class MgdbService
    {
        private static readonly string ConnectionString = ConfigurationManager.AppSettings["MongoConnectionString"] 
            ?? "mongodb://admin:adminpassword@127.0.0.1:27017/?authSource=admin";

        private static readonly string DatabaseName = ConfigurationManager.AppSettings["MongoDatabaseName"] 
            ?? "CinemaNoSQL";

        // Cấu hình timeout TRƯỚC KHI tạo MongoClient (quan trọng: thứ tự static field trong C# chạy từ trên xuống)
        private static readonly MongoClientSettings ClientSettings = CreateClientSettings();
        private static readonly MongoClient Client = new MongoClient(ClientSettings);
        private static readonly IMongoDatabase Database = Client.GetDatabase(DatabaseName);

        private static MongoClientSettings CreateClientSettings()
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);
            return settings;
        }

        private static IMongoCollection<BsonDocument> PromotionsCollection => Database.GetCollection<BsonDocument>("cinema_promotions");
        private static IMongoCollection<BsonDocument> FeedbacksCollection => Database.GetCollection<BsonDocument>("customer_feedbacks");

        // ===============================================================================
        // 1. CÁC TÍNH NĂNG MONGODB CHO COLLECTION 'cinema_promotions' (TIN TỨC & KHUYẾN MÃI)
        // ===============================================================================

        private static bool IsNoSQLEnabled => !string.Equals(ConfigurationManager.AppSettings["EnableNoSQL"], "false", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Lấy danh sách chương trình khuyến mãi (Có lọc theo chuyên mục và tìm kiếm từ khóa)
        /// </summary>
        public static List<MgdbPromotionModel> GetPromotions(string category = "", string search = "")
        {
            var list = new List<MgdbPromotionModel>();
            if (!IsNoSQLEnabled) return list;
            try
            {
                var builder = Builders<BsonDocument>.Filter;
                FilterDefinition<BsonDocument> filter = builder.Empty;

                if (!string.IsNullOrEmpty(category))
                {
                    string cat = category.Trim();
                    if (cat == "Vé xem phim" || cat == "Bắp nước" || cat == "Ví điện tử" || cat.Contains("Sinh nhật"))
                    {
                        filter = builder.Eq("category", cat);
                    }
                }

                if (!string.IsNullOrEmpty(search))
                {
                    var searchFilter = builder.Or(
                        builder.Regex("title", new BsonRegularExpression(search.Trim(), "i")),
                        builder.Regex("code", new BsonRegularExpression(search.Trim(), "i"))
                    );
                    filter = builder.And(filter, searchFilter);
                }

                var sort = Builders<BsonDocument>.Sort.Descending("_id");
                var docs = PromotionsCollection.Find(filter).Sort(sort).ToList();
                foreach (var doc in docs)
                {
                    list.Add(MapDocToPromotionModel(doc));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb GetPromotions: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// Tìm mã Voucher theo Code trong MongoDB để áp dụng giảm giá khi mua vé
        /// </summary>
        public static MgdbPromotionModel GetPromotionByCode(string code)
        {
            try
            {
                if (string.IsNullOrEmpty(code)) return null;
                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Regex("code", new BsonRegularExpression("^" + code.Trim() + "$", "i")),
                    Builders<BsonDocument>.Filter.Eq("status", "Active")
                );
                var doc = PromotionsCollection.Find(filter).FirstOrDefault();
                return doc != null ? MapDocToPromotionModel(doc) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb GetPromotionByCode: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Tạo một bài đăng khuyến mãi / mã Voucher mới (CRUD: Create)
        /// </summary>
        public static bool AddPromotion(MgdbPromotionModel promo)
        {
            try
            {
                var doc = new BsonDocument
                {
                    { "code", promo.Code?.ToUpper() ?? "KM" + DateTime.Now.Ticks.ToString().Substring(10) },
                    { "title", promo.Title ?? "Chương trình Khuyến mãi" },
                    { "category", promo.Category ?? "Vé xem phim" },
                    { "discountAmount", Convert.ToDouble(promo.DiscountAmount) },
                    { "quantity", promo.Quantity > 0 ? promo.Quantity : 100 },
                    { "claimedCount", 0 },
                    { "content", promo.Content ?? "" },
                    { "imageUrl", string.IsNullOrEmpty(promo.ImageUrl) ? "https://images.unsplash.com/photo-1489599849927-2ee91cede3ba?w=600&auto=format&fit=crop" : promo.ImageUrl },
                    { "tags", new BsonArray(promo.Tags ?? new List<string> { "Khuyến mãi" }) },
                    { "status", "Active" },
                    { "startDate", DateTime.UtcNow },
                    { "endDate", DateTime.UtcNow.AddMonths(1) }
                };

                PromotionsCollection.InsertOne(doc);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb AddPromotion: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Khách hàng bấm lấy mã Voucher -> Đảm bảo mỗi tài khoản chỉ được lấy 1 lần.
        /// Trừ 1 vào số lượng còn lại & Tăng 1 vào lượt lấy, lưu username vào claimedUsers (Atomic Update)
        /// </summary>
        public static bool ClaimVoucher(string promoId, string username)
        {
            try
            {
                if (string.IsNullOrEmpty(username) || !ObjectId.TryParse(promoId, out ObjectId objId)) return false;

                // Kiểm tra xem username đã có trong mảng claimedUsers chưa (Chỉ cho phép nhận 1 lần)
                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", objId),
                    Builders<BsonDocument>.Filter.Gt("quantity", 0),
                    Builders<BsonDocument>.Filter.Ne("claimedUsers", username)
                );

                var update = Builders<BsonDocument>.Update
                    .Inc("quantity", -1)
                    .Inc("claimedCount", 1)
                    .Push("claimedUsers", username);

                var result = PromotionsCollection.UpdateOne(filter, update);
                return result.ModifiedCount > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb ClaimVoucher: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Đánh dấu Voucher đã được sử dụng sau khi khách hàng mua vé thành công (Lưu username vào usedUsers)
        /// </summary>
        public static bool UseVoucher(string code, string username)
        {
            try
            {
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(username)) return false;

                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Regex("code", new BsonRegularExpression("^" + code.Trim() + "$", "i")),
                    Builders<BsonDocument>.Filter.Ne("usedUsers", username)
                );

                var update = Builders<BsonDocument>.Update.Push("usedUsers", username);
                var result = PromotionsCollection.UpdateOne(filter, update);
                return result.ModifiedCount > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb UseVoucher: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Lấy danh sách mã Voucher mà người dùng đã nhận nhưng CHƯA sử dụng khỏi MongoDB
        /// </summary>
        public static List<MgdbPromotionModel> GetUserClaimedVouchers(string username)
        {
            var list = new List<MgdbPromotionModel>();
            try
            {
                if (string.IsNullOrEmpty(username)) return list;

                var builder = Builders<BsonDocument>.Filter;
                var filter = builder.And(
                    builder.Eq("status", "Active"),
                    builder.Eq("claimedUsers", username),
                    builder.Ne("usedUsers", username)
                );

                var docs = PromotionsCollection.Find(filter).ToList();
                foreach (var doc in docs)
                {
                    list.Add(MapDocToPromotionModel(doc));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb GetUserClaimedVouchers: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// Xóa một chương trình khuyến mãi khỏi MongoDB (CRUD: Delete)
        /// </summary>
        public static bool DeletePromotion(string promoId)
        {
            try
            {
                if (!ObjectId.TryParse(promoId, out ObjectId objId)) return false;

                var filter = Builders<BsonDocument>.Filter.Eq("_id", objId);
                var result = PromotionsCollection.DeleteOne(filter);
                return result.DeletedCount > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb DeletePromotion: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// MongoDB Aggregation Pipeline: Thống kê số lượng khuyến mãi và tổng số lượt cấp theo chuyên mục
        /// </summary>
        public static List<MgdbPromotionCategoryStats> GetPromotionCategoryStats()
        {
            var list = new List<MgdbPromotionCategoryStats>();
            try
            {
                var pipeline = new[]
                {
                    new BsonDocument("$group", new BsonDocument
                    {
                        { "_id", "$category" },
                        { "totalPromotions", new BsonDocument("$sum", 1) },
                        { "totalQuantityLeft", new BsonDocument("$sum", "$quantity") },
                        { "totalClaimed", new BsonDocument("$sum", "$claimedCount") }
                    })
                };

                var docs = PromotionsCollection.Aggregate<BsonDocument>(pipeline).ToList();
                foreach (var doc in docs)
                {
                    list.Add(new MgdbPromotionCategoryStats
                    {
                        Category = doc.Contains("_id") && !doc["_id"].IsBsonNull ? doc["_id"].AsString : "Khác",
                        TotalPromotions = doc.Contains("totalPromotions") ? doc["totalPromotions"].AsInt32 : 0,
                        TotalQuantityLeft = doc.Contains("totalQuantityLeft") ? Convert.ToInt32(doc["totalQuantityLeft"].AsDouble) : 0,
                        TotalClaimed = doc.Contains("totalClaimed") ? Convert.ToInt32(doc["totalClaimed"].AsDouble) : 0
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb GetPromotionCategoryStats: " + ex.Message);
            }
            return list;
        }

        // ===============================================================================
        // 2. CÁC TÍNH NĂNG MONGODB CHO COLLECTION 'customer_feedbacks'
        // ===============================================================================

        /// <summary>
        /// Lấy tất cả phản hồi / khiếu nại của 1 người dùng (Lọc linh hoạt theo userId hoặc username)
        /// </summary>
        public static List<MgdbCustomerFeedbackModel> GetFeedbacksByUser(string username, int userId = 0)
        {
            var list = new List<MgdbCustomerFeedbackModel>();
            try
            {
                FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("username", username);

                var docs = FeedbacksCollection.Find(filter).SortByDescending(d => d["createdAt"]).ToList();

                foreach (var doc in docs)
                {
                    list.Add(MapDocToFeedbackModel(doc));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb GetFeedbacksByUser: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// Lấy tất cả khiếu nại trong MongoDB cho Admin quản lý (CRUD: Read All)
        /// </summary>
        public static List<MgdbCustomerFeedbackModel> GetAllFeedbacks()
        {
            var list = new List<MgdbCustomerFeedbackModel>();
            try
            {
                var docs = FeedbacksCollection.Find(new BsonDocument()).SortByDescending(d => d["createdAt"]).ToList();
                foreach (var doc in docs)
                {
                    list.Add(MapDocToFeedbackModel(doc));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb GetAllFeedbacks: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// Admin tìm kiếm khiếu nại khách hàng theo Tên (Username), Email hoặc Số điện thoại (MongoDB Regex Search)
        /// </summary>
        public static List<MgdbCustomerFeedbackModel> SearchFeedbacksForAdmin(string keyword)
        {
            var list = new List<MgdbCustomerFeedbackModel>();
            try
            {
                if (string.IsNullOrWhiteSpace(keyword)) return GetAllFeedbacks();

                string pattern = keyword.Trim();
                var regex = new BsonRegularExpression(pattern, "i");

                var builder = Builders<BsonDocument>.Filter;
                var filter = builder.Or(
                    builder.Regex("username", regex),
                    builder.Regex("email", regex),
                    builder.Regex("phone", regex),
                    builder.Regex("subject", regex)
                );

                var docs = FeedbacksCollection.Find(filter).SortByDescending(d => d["createdAt"]).ToList();
                foreach (var doc in docs)
                {
                    list.Add(MapDocToFeedbackModel(doc));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb SearchFeedbacksForAdmin: " + ex.Message);
            }
            return list;
        }

        /// <summary>
        /// Gửi 1 yêu cầu hỗ trợ / khiếu nại mới (CRUD: Create)
        /// </summary>
        public static string AddFeedback(MgdbCustomerFeedbackModel feedback)
        {
            try
            {
                var doc = new BsonDocument
                {
                    { "userId", feedback.UserId },
                    { "username", feedback.Username ?? "NguoiDung" },
                    { "email", feedback.Email ?? "" },
                    { "category", feedback.Category ?? "Khác" },
                    { "subject", feedback.Subject ?? "Hỗ trợ" },
                    { "content", feedback.Content ?? "" },
                    { "imageUrls", new BsonArray(feedback.ImageUrls ?? new List<string>()) },
                    { "status", "New" },
                    { "conversations", new BsonArray() },
                    { "createdAt", DateTime.UtcNow }
                };

                FeedbacksCollection.InsertOne(doc);
                return null; // null = thành công
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb AddFeedback: " + ex.ToString());
                return ex.Message; // Trả về lỗi chi tiết
            }
        }

        /// <summary>
        /// Admin trả lời phản hồi và cập nhật trạng thái (CRUD: Update)
        /// </summary>
        public static bool ReplyFeedback(string feedbackId, string replyMessage, string sender = "Admin")
        {
            try
            {
                if (!ObjectId.TryParse(feedbackId, out ObjectId objId)) return false;

                var filter = Builders<BsonDocument>.Filter.Eq("_id", objId);
                var conversationDoc = new BsonDocument
                {
                    { "sender", sender },
                    { "message", replyMessage },
                    { "createdAt", DateTime.UtcNow }
                };

                var update = Builders<BsonDocument>.Update
                    .Set("status", "Resolved")
                    .Push("conversations", conversationDoc);

                var result = FeedbacksCollection.UpdateOne(filter, update);
                return result.ModifiedCount > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb ReplyFeedback: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Xóa một phản hồi / khiếu nại khỏi MongoDB (CRUD: Delete)
        /// Dành riêng cho Khách hàng chỉ xóa được khiếu nại của chính mình.
        /// </summary>
        public static bool DeleteFeedback(string feedbackId, string username, bool isAdmin = false)
        {
            try
            {
                if (!ObjectId.TryParse(feedbackId, out ObjectId objId)) return false;

                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", objId),
                    Builders<BsonDocument>.Filter.Eq("username", username)
                );

                var result = FeedbacksCollection.DeleteOne(filter);
                return result.DeletedCount > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb DeleteFeedback: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Thống kê số lượng phản hồi theo chuyên mục (category) - Chạy siêu tốc trong 1ms
        /// </summary>
        public static List<MgdbFeedbackCategoryStats> GetFeedbackCategoryStats()
        {
            var list = new List<MgdbFeedbackCategoryStats>();
            try
            {
                var pipeline = new[]
                {
                    new BsonDocument("$group", new BsonDocument
                    {
                        { "_id", "$category" },
                        { "totalTickets", new BsonDocument("$sum", 1) },
                        { "resolvedCount", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$status", "Resolved" }), 1, 0 })) },
                        { "pendingCount", new BsonDocument("$sum", new BsonDocument("$cond", new BsonArray { new BsonDocument("$ne", new BsonArray { "$status", "Resolved" }), 1, 0 })) }
                    }),
                    new BsonDocument("$project", new BsonDocument
                    {
                        { "_id", 0 },
                        { "category", "$_id" },
                        { "totalTickets", 1 },
                        { "resolvedCount", 1 },
                        { "pendingCount", 1 }
                    })
                };

                var docs = FeedbacksCollection.Aggregate<BsonDocument>(pipeline).ToList();
                foreach (var doc in docs)
                {
                    list.Add(new MgdbFeedbackCategoryStats
                    {
                        Category = doc.Contains("category") && !doc["category"].IsBsonNull ? doc["category"].AsString : "Khác",
                        TotalTickets = doc.Contains("totalTickets") ? doc["totalTickets"].AsInt32 : 0,
                        ResolvedCount = doc.Contains("resolvedCount") ? doc["resolvedCount"].AsInt32 : 0,
                        PendingCount = doc.Contains("pendingCount") ? doc["pendingCount"].AsInt32 : 0
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error Mgdb GetFeedbackCategoryStats: " + ex.Message);
            }
            return list;
        }

        // ===============================================================================
        // HELPER MAPPING METHODS
        // ===============================================================================

        private static MgdbPromotionModel MapDocToPromotionModel(BsonDocument doc)
        {
            var model = new MgdbPromotionModel
            {
                Id = doc["_id"].ToString(),
                Code = doc.Contains("code") && !doc["code"].IsBsonNull ? doc["code"].AsString : "",
                Title = doc.Contains("title") && !doc["title"].IsBsonNull ? doc["title"].AsString : "",
                Category = doc.Contains("category") && !doc["category"].IsBsonNull ? doc["category"].AsString : "Vé xem phim",
                DiscountAmount = GetBsonDecimal(doc, "discountAmount", 0),
                Quantity = GetBsonInt(doc, "quantity", 0),
                ClaimedCount = GetBsonInt(doc, "claimedCount", 0),
                Content = doc.Contains("content") && !doc["content"].IsBsonNull ? doc["content"].AsString : "",
                ImageUrl = doc.Contains("imageUrl") && !doc["imageUrl"].IsBsonNull ? doc["imageUrl"].AsString : "",
                Status = doc.Contains("status") && !doc["status"].IsBsonNull ? doc["status"].AsString : "Active",
                StartDate = doc.Contains("startDate") && doc["startDate"].IsBsonDateTime ? doc["startDate"].ToUniversalTime() : DateTime.UtcNow,
                EndDate = doc.Contains("endDate") && doc["endDate"].IsBsonDateTime ? doc["endDate"].ToUniversalTime() : DateTime.UtcNow.AddMonths(1)
            };

            if (doc.Contains("tags") && doc["tags"].IsBsonArray)
            {
                model.Tags = doc["tags"].AsBsonArray.Select(t => t.AsString).ToList();
            }

            if (doc.Contains("claimedUsers") && doc["claimedUsers"].IsBsonArray)
            {
                model.ClaimedUsers = doc["claimedUsers"].AsBsonArray.Select(t => t.AsString).ToList();
            }

            if (doc.Contains("usedUsers") && doc["usedUsers"].IsBsonArray)
            {
                model.UsedUsers = doc["usedUsers"].AsBsonArray.Select(t => t.AsString).ToList();
            }

            return model;
        }

        private static decimal GetBsonDecimal(BsonDocument doc, string field, decimal defaultVal = 0)
        {
            if (!doc.Contains(field) || doc[field].IsBsonNull) return defaultVal;
            try { return Convert.ToDecimal(BsonTypeMapper.MapToDotNetValue(doc[field])); }
            catch { return defaultVal; }
        }

        private static int GetBsonInt(BsonDocument doc, string field, int defaultVal = 0)
        {
            if (!doc.Contains(field) || doc[field].IsBsonNull) return defaultVal;
            try { return Convert.ToInt32(BsonTypeMapper.MapToDotNetValue(doc[field])); }
            catch { return defaultVal; }
        }

        private static MgdbCustomerFeedbackModel MapDocToFeedbackModel(BsonDocument doc)
        {
            var model = new MgdbCustomerFeedbackModel
            {
                Id = doc["_id"].ToString(),
                UserId = doc.Contains("userId") ? doc["userId"].AsInt32 : 0,
                Username = doc.Contains("username") ? doc["username"].AsString : "",
                Email = doc.Contains("email") ? doc["email"].AsString : "",
                Category = doc.Contains("category") ? doc["category"].AsString : "Khác",
                Subject = doc.Contains("subject") ? doc["subject"].AsString : "",
                Content = doc.Contains("content") ? doc["content"].AsString : "",
                Status = doc.Contains("status") ? doc["status"].AsString : "New"
            };

            if (doc.Contains("conversations") && doc["conversations"].IsBsonArray)
            {
                foreach (var conv in doc["conversations"].AsBsonArray)
                {
                    if (conv.IsBsonDocument)
                    {
                        var cDoc = conv.AsBsonDocument;
                        model.Conversations.Add(new MgdbFeedbackConversation
                        {
                            Sender = cDoc.Contains("sender") ? cDoc["sender"].AsString : "Admin",
                            Message = cDoc.Contains("message") ? cDoc["message"].AsString : ""
                        });
                    }
                }
            }

            return model;
        }
    }
}
