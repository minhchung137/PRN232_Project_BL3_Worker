using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PRN232_GradingSystem_Worker_Services.Interfaces;

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    public sealed class SqlServerDbResetService : IDbResetService
    {
        private readonly string _connectionString;

        public SqlServerDbResetService(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public async Task ResetAsync(string sqlScript, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sqlScript))
            {
                throw new ArgumentException("SQL script is empty", nameof(sqlScript));
            }

            var batches = SplitSqlBatches(sqlScript);
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var batch in batches)
            {
                await using var cmd = new SqlCommand(batch, conn)
                {
                    CommandType = CommandType.Text,
                    CommandTimeout = 120
                };
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static IReadOnlyList<string> SplitSqlBatches(string script)
        {
            // Split on lines that contain only GO (case-insensitive), ignoring GO in strings/comments is hard; assume instructor script is clean
            var regex = new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var parts = regex.Split(script);
            var result = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    result.Add(trimmed);
                }
            }
            return result;
        }
    }
}
