using System.Data.Common;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace ApiMonitor.Api;

/// <summary>
/// 数据库方言抽象：同时支持 SQLite（默认）与 MySQL。
/// 由配置 Database:Provider = sqlite|mysql 决定，SQL 统一用 @参数（两种库都支持）。
/// </summary>
public sealed class Db(IConfiguration config, ILogger<Db> logger)
{
    public bool IsMySql => string.Equals(config["Database:Provider"], "mysql", StringComparison.OrdinalIgnoreCase);
    private string ConnectionString => config.GetConnectionString("Default") ?? "Data Source=api-monitor.db";

    /// <summary>MySQL 下若目标库不存在则自动创建；SQLite 建文件即可。</summary>
    public void EnsureDatabase()
    {
        if (!IsMySql) return;
        var builder = new MySqlConnectionStringBuilder(ConnectionString);
        if (string.IsNullOrWhiteSpace(builder.Database)) return;
        var database = builder.Database;
        builder.Database = null;
        using var connection = new MySqlConnection(builder.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{database.Replace("`", "``")}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
        cmd.ExecuteNonQuery();
        logger.LogInformation("MySQL 数据库 {Database} 已就绪", database);
    }

    public DbConnection Open()
    {
        if (IsMySql) { var db = new MySqlConnection(ConnectionString); db.Open(); return db; }
        var sqlite = new SqliteConnection(ConnectionString); sqlite.Open(); return sqlite;
    }

    /// <summary>自增主键表定义：SQLite 用 INTEGER AUTOINCREMENT，MySQL 用 BIGINT AUTO_INCREMENT。</summary>
    public string IdColumn => IsMySql ? "id BIGINT PRIMARY KEY AUTO_INCREMENT" : "id INTEGER PRIMARY KEY AUTOINCREMENT";
    public string TextType => IsMySql ? "LONGTEXT" : "TEXT";
    /// <summary>MySQL 的 TEXT/LONGTEXT 列不允许 DEFAULT 值，SQLite 保留默认值方便旧表补列。</summary>
    public string TextNotNull => IsMySql ? $"{TextType} NOT NULL" : $"{TextType} NOT NULL DEFAULT ''";

    /// <summary>建索引：SQLite 支持 IF NOT EXISTS；MySQL 靠捕获已存在错误实现幂等。</summary>
    public void TryCreateIndex(DbConnection db, string name, string table, string columns)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = IsMySql
            ? $"CREATE INDEX {name} ON {table} ({columns})"
            : $"CREATE INDEX IF NOT EXISTS {name} ON {table} ({columns})";
        try { cmd.ExecuteNonQuery(); }
        catch (Exception ex) { logger.LogDebug(ex, "索引 {Index} 创建跳过（可能已存在）", name); }
    }

    /// <summary>给旧表补列，列已存在时静默忽略。</summary>
    public void TryAddColumn(DbConnection db, string table, string name, string definition)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition}";
        try { cmd.ExecuteNonQuery(); }
        catch (Exception ex) { logger.LogDebug(ex, "列 {Table}.{Column} 补充跳过（可能已存在）", table, name); }
    }

    /// <summary>取最近一条 INSERT 的自增 id。</summary>
    public long LastInsertId(DbConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = IsMySql ? "SELECT LAST_INSERT_ID()" : "SELECT last_insert_rowid()";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
