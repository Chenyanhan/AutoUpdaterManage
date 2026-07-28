using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using MySqlConnector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoUpdater.Client.Net462
{
    internal static class WindingMachineConfigReader
    {
        public static string ReadConnectionString(
            string installationDirectory,
            string configFileName)
        {
            var path = Path.Combine(
                Path.GetFullPath(installationDirectory),
                string.IsNullOrWhiteSpace(configFileName)
                    ? "卷绕机.exe.config"
                    : configFileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "找不到卷绕机数据库配置。", path);

            var document = XDocument.Load(path, LoadOptions.None);
            var values = document
                .Descendants()
                .Where(element =>
                    string.Equals(
                        element.Name.LocalName,
                        "add",
                        StringComparison.OrdinalIgnoreCase))
                .SelectMany(element => new[]
                {
                    (string)element.Attribute("connectionString"),
                    (string)element.Attribute("value")
                })
                .Where(value => !string.IsNullOrWhiteSpace(value));
            foreach (var value in values)
            {
                try
                {
                    var builder = new MySqlConnectionStringBuilder(value);
                    if (string.IsNullOrWhiteSpace(builder.Server) ||
                        string.IsNullOrWhiteSpace(builder.Database) ||
                        string.IsNullOrWhiteSpace(builder.UserID))
                        continue;
                    builder.ConnectionTimeout = 10;
                    builder.DefaultCommandTimeout = 30;
                    builder.Pooling = true;
                    return builder.ConnectionString;
                }
                catch (ArgumentException)
                {
                    // 不是MySQL连接串。
                }
            }
            throw new InvalidDataException(
                "卷绕机.exe.config 中没有找到标准MySQL连接串。");
        }
    }

    internal static class DatabaseSyncService
    {
        private const long MaxPackageSize = 100L * 1024 * 1024;
        private static readonly HashSet<string> AllowedTables =
            new HashSet<string>(
                new[] { "data_result", "plc_user_manage" },
                StringComparer.OrdinalIgnoreCase);

        public static DatabaseSyncPackage LoadAndVerifyPackage(
            DatabaseSyncFileRequestPayload request)
        {
            if (request.PackageSize <= 0 ||
                request.PackageSize > MaxPackageSize)
                throw new InvalidDataException(
                    "同步包大小必须在1字节到100MB之间。");
            var path = Path.GetFullPath(request.PackagePath);
            var file = new FileInfo(path);
            if (!file.Exists)
                throw new FileNotFoundException(
                    "找不到数据库同步包。", path);
            if (file.Length != request.PackageSize)
                throw new InvalidDataException(
                    "数据库同步包大小不一致。");

            byte[] actualHash;
            using (var stream = File.Open(
                       path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha256 = SHA256.Create())
                actualHash = sha256.ComputeHash(stream);
            byte[] expectedHash;
            try
            {
                expectedHash = HexToBytes(request.Sha256);
            }
            catch
            {
                throw new InvalidDataException("SHA-256格式无效。");
            }
            if (!FixedEquals(expectedHash, actualHash))
                throw new InvalidDataException(
                    "同步包SHA-256校验失败，文件可能已损坏或被篡改。");

            var package = JsonConvert.DeserializeObject<DatabaseSyncPackage>(
                File.ReadAllText(path));
            if (package == null)
                throw new InvalidDataException("同步包内容为空。");
            if (package.SchemaVersion != 1)
                throw new InvalidDataException(
                    "不支持同步包版本：" + package.SchemaVersion);
            if (package.Changes == null ||
                package.Changes.Count == 0 ||
                package.Changes.Count > 500)
                throw new InvalidDataException(
                    "同步包变更数量必须在1到500条之间。");
            ValidateChanges(package.Changes);
            return package;
        }

        public static int Execute(
            string connectionString,
            DatabaseSyncPackage package)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            if (!string.Equals(
                    builder.Database,
                    package.DatabaseName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "同步包数据库与卷绕机配置不一致。");

            using (var connection = new MySqlConnection(
                       builder.ConnectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var affectedRows = 0;
                        foreach (var change in package.Changes)
                        {
                            using (var command = connection.CreateCommand())
                            {
                                command.Transaction = transaction;
                                BuildCommand(command, change);
                                var affected = command.ExecuteNonQuery();
                                if ((change.Operation == "UPDATE" ||
                                     change.Operation == "DELETE") &&
                                    affected != 1)
                                    throw new InvalidOperationException(
                                        change.Operation + " " +
                                        change.TableName +
                                        " 应影响1行，实际影响" +
                                        affected + "行。");
                                affectedRows += affected;
                            }
                        }
                        transaction.Commit();
                        return affectedRows;
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        private static void ValidateChanges(
            IEnumerable<DatabaseChangePayload> changes)
        {
            foreach (var change in changes)
            {
                if (!AllowedTables.Contains(change.TableName))
                    throw new InvalidDataException(
                        "不允许同步数据表：" + change.TableName);
                if (change.Operation != "INSERT" &&
                    change.Operation != "UPDATE" &&
                    change.Operation != "DELETE")
                    throw new InvalidDataException(
                        "不支持数据库操作：" + change.Operation);
                if ((change.Operation == "UPDATE" ||
                     change.Operation == "DELETE") &&
                    (change.KeyValues == null ||
                     change.KeyValues.Count == 0))
                    throw new InvalidDataException(
                        change.Operation + " 缺少主键。");
            }
        }

        private static void BuildCommand(
            MySqlCommand command,
            DatabaseChangePayload change)
        {
            var table = Quote(change.TableName);
            var values = change.Values ??
                         new Dictionary<string, JToken>();
            var keys = change.KeyValues ??
                       new Dictionary<string, JToken>();
            if (change.Operation == "INSERT")
            {
                if (values.Count == 0)
                    throw new InvalidOperationException("INSERT没有字段。");
                command.CommandText =
                    "INSERT INTO " + table + " (" +
                    string.Join(", ", values.Keys.Select(Quote)) +
                    ") VALUES (" +
                    string.Join(", ", values.Keys.Select(
                        (name, index) => "@v" + index)) + ");";
                AddParameters(command, values, "v");
                return;
            }
            if (change.Operation == "UPDATE")
            {
                if (values.Count == 0)
                    throw new InvalidOperationException("UPDATE没有字段。");
                command.CommandText =
                    "UPDATE " + table + " SET " +
                    string.Join(", ", values.Keys.Select(
                        (name, index) =>
                            Quote(name) + "=@v" + index)) +
                    " WHERE " +
                    string.Join(" AND ", keys.Keys.Select(
                        (name, index) =>
                            Quote(name) + " <=> @k" + index)) + ";";
                AddParameters(command, values, "v");
                AddParameters(command, keys, "k");
                return;
            }
            command.CommandText =
                "DELETE FROM " + table + " WHERE " +
                string.Join(" AND ", keys.Keys.Select(
                    (name, index) =>
                        Quote(name) + " <=> @k" + index)) + ";";
            AddParameters(command, keys, "k");
        }

        private static void AddParameters(
            MySqlCommand command,
            IDictionary<string, JToken> values,
            string prefix)
        {
            var index = 0;
            foreach (var value in values.Values)
                command.Parameters.AddWithValue(
                    "@" + prefix + index++,
                    ConvertValue(value));
        }

        private static object ConvertValue(JToken value)
        {
            if (value == null ||
                value.Type == JTokenType.Null ||
                value.Type == JTokenType.Undefined)
                return DBNull.Value;
            var token = value as JValue;
            return token == null ? value.ToString(Formatting.None) :
                token.Value ?? DBNull.Value;
        }

        private static string Quote(string value)
        {
            return "`" + value.Replace("`", "``") + "`";
        }

        private static byte[] HexToBytes(string text)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                text.Length % 2 != 0)
                throw new FormatException();
            var result = new byte[text.Length / 2];
            for (var i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(
                    text.Substring(i * 2, 2), 16);
            return result;
        }

        private static bool FixedEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null ||
                left.Length != right.Length)
                return false;
            var difference = 0;
            for (var i = 0; i < left.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
