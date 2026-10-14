using System.Data.Common;
using System.Security.Cryptography;

namespace ApiMonitor.Api;

public sealed class AppUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>PBKDF2-SHA256 密码散列，格式 pbkdf2$迭代$盐$哈希。</summary>
public static class Passwords
{
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

public sealed class UserStore(Db db, ILogger<UserStore> logger)
{
    private readonly object gate = new();

    /// <summary>建表；若没有任何用户则种子一个 admin（admin123），返回种子账号（否则 null）。</summary>
    public AppUser? Initialize()
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS app_users (
                    {db.IdColumn},
                    username {(db.IsMySql ? "VARCHAR(64)" : "TEXT")} NOT NULL UNIQUE,
                    password_hash {(db.IsMySql ? "VARCHAR(255)" : "TEXT")} NOT NULL,
                    created_at {(db.IsMySql ? "VARCHAR(64)" : "TEXT")} NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
            db.TryCreateIndex(connection, "idx_users_username", "app_users", "username");
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM app_users";
            if (Convert.ToInt64(count.ExecuteScalar()) > 0) return null;
            var admin = new AppUser { Username = "admin", PasswordHash = Passwords.Hash("admin123"), CreatedAt = DateTime.UtcNow };
            Insert(connection, admin);
            logger.LogWarning("已创建默认管理员 admin / admin123（存量监控任务将归属该账号），请登录后尽快修改密码");
            return admin;
        }
    }

    private void Insert(DbConnection connection, AppUser user)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO app_users(username, password_hash, created_at) VALUES (@username, @hash, @at)";
        Add(cmd, "@username", user.Username);
        Add(cmd, "@hash", user.PasswordHash);
        Add(cmd, "@at", user.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        user.Id = db.LastInsertId(connection);
    }

    private static void Add(DbCommand cmd, string name, object value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p); }

    public AppUser? FindByName(string username)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, created_at FROM app_users WHERE username = @username";
            Add(cmd, "@username", username);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public AppUser? Find(long id)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, username, password_hash, created_at FROM app_users WHERE id = @id";
            Add(cmd, "@id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public AppUser Create(string username, string password)
    {
        lock (gate)
        {
            if (FindByName(username) is not null) throw new InvalidOperationException("用户名已被占用");
            var user = new AppUser { Username = username, PasswordHash = Passwords.Hash(password), CreatedAt = DateTime.UtcNow };
            using var connection = db.Open();
            Insert(connection, user);
            return user;
        }
    }

    private static AppUser Read(DbDataReader reader) => new()
    {
        Id = Convert.ToInt64(reader.GetValue(0)),
        Username = reader.GetString(1),
        PasswordHash = reader.GetString(2),
        CreatedAt = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };
}
