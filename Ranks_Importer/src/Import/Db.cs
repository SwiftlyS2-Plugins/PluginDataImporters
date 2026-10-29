using System.Data;

namespace Ranks.Importer.Import;

/// <summary>Small ADO helpers, so every statement in the importer reads as one line.</summary>
/// <remarks>
/// Raw ADO rather than an ORM: the importer runs against whichever of MySQL, PostgreSQL or SQLite
/// Ranks is on, and the three share no mapper here. Every value is parameterised; the only strings
/// concatenated into SQL are table names, which come from config and from core's own resolver.
/// </remarks>
internal static class Db
{
    public static IDbCommand Command(IDbConnection connection, IDbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        return command;
    }

    public static void Bind(IDbCommand command, string name, object? value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;

        command.Parameters.Add(p);
    }

    public static long Scalar(IDbConnection connection, string sql)
    {
        using var command = Command(connection, null, sql);
        var result = command.ExecuteScalar();

        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>Whether the table exists AND is readable by this account.</summary>
    /// <remarks>
    /// A probe rather than an information_schema query: that view is spelled differently on all
    /// three engines, and readability is the thing the importer actually needs — a shared host can
    /// grant SELECT on one table and not another.
    /// </remarks>
    public static bool TableReadable(IDbConnection connection, string table)
    {
        try
        {
            using var command = Command(connection, null, $"SELECT 1 FROM {table} WHERE 1 = 0");
            command.ExecuteNonQuery();

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the column exists AND is readable.</summary>
    /// <remarks>
    /// K4-Zenith adds a column per module by ALTER TABLE the first time that module loads, so a
    /// server that never ran one simply has no such column. Probing beats information_schema for
    /// the same reason <see cref="TableReadable"/> does.
    /// </remarks>
    public static bool ColumnReadable(IDbConnection connection, string table, string column)
    {
        try
        {
            using var command = Command(connection, null, $"SELECT {column} FROM {table} WHERE 1 = 0");
            command.ExecuteNonQuery();

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int GetInt32(IDataRecord row, int i) => row.IsDBNull(i) ? 0 : Convert.ToInt32(row.GetValue(i));

    public static long GetInt64(IDataRecord row, int i) => row.IsDBNull(i) ? 0L : Convert.ToInt64(row.GetValue(i));

    public static string GetString(IDataRecord row, int i) => row.IsDBNull(i) ? string.Empty : row.GetValue(i).ToString() ?? string.Empty;
}
