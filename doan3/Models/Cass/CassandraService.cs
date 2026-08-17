using System;
using System.Configuration;
using Cassandra;

namespace doan3.Models.Cass
{
    /// <summary>
    /// ============================================================================
    /// MODULE CASSANDRA - HOÀN TOÀN ĐỘC LẬP
    /// ============================================================================
    /// - Không đụng tới RedisService / MgdbService / Neo4jService / Entity Framework.
    /// - Theo đúng style code hiện có của dự án: KHÔNG dùng DI Framework (Autofac/Ninject),
    ///   dùng Singleton dạng Static Holder + Lazy&lt;T&gt; (giống RedisService.cs).
    /// - Nếu xoá toàn bộ thư mục Models/Cass thì website vẫn build và chạy bình thường,
    ///   vì không có Controller/Service nào khác tham chiếu ngược lại module này.
    /// - Đọc cấu hình từ Web.config (appSettings: CassandraContactPoints, CassandraPort,
    ///   CassandraKeyspace) - các key này chỉ được THÊM MỚI, không sửa key cũ.
    /// ============================================================================
    /// </summary>
    public static class CassandraService
    {
        private static readonly Lazy<Cluster> LazyCluster;
        private static readonly Lazy<ISession> LazySession;

        static CassandraService()
        {
            LazyCluster = new Lazy<Cluster>(() =>
            {
                string contactPointsRaw = ConfigurationManager.AppSettings["CassandraContactPoints"] ?? "127.0.0.1";
                string portRaw = ConfigurationManager.AppSettings["CassandraPort"] ?? "9042";

                int port;
                if (!int.TryParse(portRaw, out port) || port <= 0)
                {
                    port = 9042;
                }

                var contactPoints = contactPointsRaw.Split(
                    new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                var builder = Cluster.Builder()
                                     .AddContactPoints(contactPoints)
                                     .WithPort(port);

                return builder.Build();
            });

            LazySession = new Lazy<ISession>(() =>
            {
                string keyspace = ConfigurationManager.AppSettings["CassandraKeyspace"] ?? "cinema_history";
                // Keyspace và bảng phải được tạo trước bằng Scripts/Cassandra/01_create_database.cql
                return LazyCluster.Value.Connect(keyspace);
            });
        }

        /// <summary>
        /// Lấy Session (Singleton) để thực thi CQL. Có thể throw Exception nếu Cassandra
        /// chưa chạy / chưa tạo Keyspace - nơi gọi (CassandraFeaturesService) BẮT BUỘC
        /// phải try/catch để không làm ảnh hưởng luồng nghiệp vụ chính (SQL Server).
        /// </summary>
        public static ISession GetSession()
        {
            if (string.Equals(ConfigurationManager.AppSettings["EnableNoSQL"], "false", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return LazySession.Value;
        }
    }
}
