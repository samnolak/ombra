using Npgsql;

namespace Ombra.Server.Data;

/// <summary>Thin helpers over Npgsql: parameters are always bound, never interpolated.</summary>
public static class Sql
{
    public static NpgsqlCommand Cmd(this NpgsqlConnection conn, string sql, NpgsqlTransaction? tx, params (string Name, object? Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static async Task<int> ExecAsync(this NpgsqlConnection conn, string sql, NpgsqlTransaction? tx, params (string, object?)[] args)
    {
        await using var cmd = conn.Cmd(sql, tx, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(this NpgsqlConnection conn, string sql, NpgsqlTransaction? tx, params (string, object?)[] args)
    {
        await using var cmd = conn.Cmd(sql, tx, args);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)v;
    }

    public static async Task<List<T>> ListAsync<T>(this NpgsqlConnection conn, string sql, NpgsqlTransaction? tx, Func<NpgsqlDataReader, T> map, params (string, object?)[] args)
    {
        await using var cmd = conn.Cmd(sql, tx, args);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await r.ReadAsync()) list.Add(map(r));
        return list;
    }

    public static async Task<T?> SingleAsync<T>(this NpgsqlConnection conn, string sql, NpgsqlTransaction? tx, Func<NpgsqlDataReader, T> map, params (string, object?)[] args) where T : class
    {
        var list = await conn.ListAsync(sql, tx, map, args);
        return list.Count == 0 ? null : list[0];
    }

    public static Guid? GuidOrNull(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    public static string? StringOrNull(this NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static byte[] Bytes(this NpgsqlDataReader r, int i) => (byte[])r[i];
    public static DateTimeOffset Time(this NpgsqlDataReader r, int i) => new(r.GetDateTime(i), TimeSpan.Zero);

    public static bool IsUniqueViolation(this PostgresException e) => e.SqlState == PostgresErrorCodes.UniqueViolation;
}
