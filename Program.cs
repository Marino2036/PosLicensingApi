using Microsoft.AspNetCore.Http.Json;
using Npgsql;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
app.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok("POS Licensing API OK"));

string raw =
    Environment.GetEnvironmentVariable("ConnectionStrings__LicensingDb")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "";

if (string.IsNullOrWhiteSpace(raw))
{
    app.MapGet("/health/db", () => Results.Problem("Falta env var: ConnectionStrings__LicensingDb (o DATABASE_URL)."));
    app.Run();
    return;
}

string connStr = BuildNpgsqlConnStr(raw);
string adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "";

Exception? schemaError = null;
try
{
    await EnsureSchemaAsync(connStr);
}
catch (Exception ex)
{
    schemaError = ex;
}

app.MapGet("/health/db", async () =>
{
    try
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT current_database() as db, now() as server_time;", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();

        return Results.Ok(new
        {
            ok = true,
            db = rd["db"]?.ToString(),
            server_time = rd["server_time"]?.ToString(),
            schema_ok = schemaError == null,
            schema_error = schemaError?.Message
        });
    }
    catch (Exception ex)
    {
        return Results.Problem("DB error: " + ex.Message + (schemaError != null ? (" | schema_error: " + schemaError.Message) : ""));
    }
});

app.MapPost("/license/validate", async (ValidateRequest req) =>
{
    try
    {
        if (req == null || string.IsNullOrWhiteSpace(req.licenseKey) || string.IsNullOrWhiteSpace(req.machineId))
            return Results.BadRequest(new { valid = false, message = "licenseKey y machineId son requeridos." });

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        const string sql = @"
SELECT id, license_key, is_active, expires_at, bound_machine_id, max_activations, activations_used
FROM licenses
WHERE license_key = @k
LIMIT 1;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("k", req.licenseKey.Trim());

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return Results.Ok(new { valid = false, message = "Licencia no existe." });

        bool isActive = rd.GetBoolean(rd.GetOrdinal("is_active"));

        DateTime? expiresAt = rd.IsDBNull(rd.GetOrdinal("expires_at"))
            ? (DateTime?)null
            : rd.GetDateTime(rd.GetOrdinal("expires_at"));

        string? boundMachine = rd.IsDBNull(rd.GetOrdinal("bound_machine_id"))
            ? null
            : rd.GetString(rd.GetOrdinal("bound_machine_id"));

        int maxActivations = rd.GetInt32(rd.GetOrdinal("max_activations"));
        int used = rd.GetInt32(rd.GetOrdinal("activations_used"));

        if (!isActive)
            return Results.Ok(new { valid = false, message = "Licencia desactivada." });

        if (expiresAt.HasValue)
        {
            var expUtc = DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc);
            if (DateTime.UtcNow > expUtc)
                return Results.Ok(new { valid = false, expiresUtc = expUtc, message = "Licencia vencida." });
        }

        if (!string.IsNullOrWhiteSpace(boundMachine) &&
            !string.Equals(boundMachine, req.machineId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { valid = false, message = "Licencia ya está activada en otra computadora." });
        }

        if (string.IsNullOrWhiteSpace(boundMachine))
        {
            if (used >= maxActivations)
                return Results.Ok(new { valid = false, message = "Límite de activaciones alcanzado." });

            await rd.CloseAsync();

            const string up = @"
UPDATE licenses
SET bound_machine_id = @m,
    activations_used = activations_used + 1
WHERE license_key = @k
  AND (bound_machine_id IS NULL OR bound_machine_id = '');";

            await using var cmdUp = new NpgsqlCommand(up, conn);
            cmdUp.Parameters.AddWithValue("m", req.machineId.Trim());
            cmdUp.Parameters.AddWithValue("k", req.licenseKey.Trim());
            await cmdUp.ExecuteNonQueryAsync();
        }

        return Results.Ok(new
        {
            valid = true,
            expiresUtc = expiresAt.HasValue
                ? DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc)
                : (DateTime?)null,
            message = "OK"
        });
    }
    catch (PostgresException pex)
    {
        return Results.Problem("Postgres error: " + pex.MessageText);
    }
    catch (Exception ex)
    {
        return Results.Problem("Server error: " + ex.Message);
    }
});

// Auth admin
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/admin/api"))
    {
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { error = "ADMIN_KEY no está configurada en Render." });
            return;
        }

        var header = ctx.Request.Headers["X-ADMIN-KEY"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) || header != adminKey)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized. Revisa X-ADMIN-KEY." });
            return;
        }
    }

    await next();
});

// Estadísticas
app.MapGet("/admin/api/stats", async () =>
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    const string sql = @"
SELECT
    COALESCE(SUM(activations_used), 0) AS total_activations,
    COALESCE(COUNT(bound_machine_id) FILTER (WHERE bound_machine_id IS NOT NULL AND bound_machine_id <> ''), 0) AS pcs_activadas,
    COALESCE(COUNT(*) FILTER (
        WHERE expires_at IS NOT NULL
          AND expires_at >= NOW()
          AND expires_at <= NOW() + INTERVAL '7 days'
    ), 0) AS vencimientos_proximos
FROM licenses;";

    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var rd = await cmd.ExecuteReaderAsync();
    await rd.ReadAsync();

    return Results.Ok(new
    {
        total_activations = Convert.ToInt32(rd["total_activations"]),
        pcs_activadas = Convert.ToInt32(rd["pcs_activadas"]),
        vencimientos_proximos = Convert.ToInt32(rd["vencimientos_proximos"])
    });
});

app.MapGet("/admin/api/licenses", async (string? q) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    q = (q ?? "").Trim();

    string sql;
    var cmd = new NpgsqlCommand { Connection = conn };

    if (string.IsNullOrWhiteSpace(q))
    {
        sql = @"
SELECT id, license_key, is_active, expires_at, bound_machine_id, max_activations, activations_used
FROM licenses
ORDER BY id DESC
LIMIT 200;";
    }
    else
    {
        sql = @"
SELECT id, license_key, is_active, expires_at, bound_machine_id, max_activations, activations_used
FROM licenses
WHERE license_key ILIKE @q OR bound_machine_id ILIKE @q
ORDER BY id DESC
LIMIT 200;";
        cmd.Parameters.AddWithValue("q", "%" + q + "%");
    }

    cmd.CommandText = sql;

    var list = new List<object>();
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        DateTime? exp = rd.IsDBNull(rd.GetOrdinal("expires_at")) ? null : rd.GetDateTime(rd.GetOrdinal("expires_at"));

        string status = "Activa";
        if (!rd.GetBoolean(rd.GetOrdinal("is_active")))
            status = "Desactivada";
        else if (exp.HasValue && DateTime.UtcNow > DateTime.SpecifyKind(exp.Value, DateTimeKind.Utc))
            status = "Vencida";
        else if (exp.HasValue && DateTime.UtcNow.AddDays(7) >= DateTime.SpecifyKind(exp.Value, DateTimeKind.Utc))
            status = "Próxima a vencer";

        list.Add(new
        {
            id = rd.GetInt32(rd.GetOrdinal("id")),
            license_key = rd.GetString(rd.GetOrdinal("license_key")),
            is_active = rd.GetBoolean(rd.GetOrdinal("is_active")),
            status = status,
            expires_at = exp.HasValue ? DateTime.SpecifyKind(exp.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : null,
            bound_machine_id = rd.IsDBNull(rd.GetOrdinal("bound_machine_id")) ? null : rd.GetString(rd.GetOrdinal("bound_machine_id")),
            max_activations = rd.GetInt32(rd.GetOrdinal("max_activations")),
            activations_used = rd.GetInt32(rd.GetOrdinal("activations_used"))
        });
    }

    return Results.Ok(list);
});

app.MapPost("/admin/api/licenses", async (AdminCreateLicense req) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.licenseKey))
        return Results.BadRequest(new { error = "licenseKey requerido" });

    int maxAct = req.maxActivations <= 0 ? 1 : req.maxActivations;

    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    const string sql = @"
INSERT INTO licenses (license_key, is_active, expires_at, bound_machine_id, max_activations, activations_used)
VALUES (@k, @a, @e, NULL, @m, 0)
RETURNING id, license_key, is_active, expires_at, bound_machine_id, max_activations, activations_used;";

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("k", req.licenseKey.Trim());
    cmd.Parameters.AddWithValue("a", req.isActive);

    if (req.expiresUtc == null) cmd.Parameters.AddWithValue("e", DBNull.Value);
    else cmd.Parameters.AddWithValue("e", req.expiresUtc.Value);

    cmd.Parameters.AddWithValue("m", maxAct);

    try
    {
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();

        DateTime? exp = rd.IsDBNull(rd.GetOrdinal("expires_at")) ? null : rd.GetDateTime(rd.GetOrdinal("expires_at"));

        return Results.Ok(new
        {
            id = rd.GetInt32(rd.GetOrdinal("id")),
            license_key = rd.GetString(rd.GetOrdinal("license_key")),
            is_active = rd.GetBoolean(rd.GetOrdinal("is_active")),
            expires_at = exp.HasValue ? DateTime.SpecifyKind(exp.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : null,
            bound_machine_id = rd.IsDBNull(rd.GetOrdinal("bound_machine_id")) ? null : rd.GetString(rd.GetOrdinal("bound_machine_id")),
            max_activations = rd.GetInt32(rd.GetOrdinal("max_activations")),
            activations_used = rd.GetInt32(rd.GetOrdinal("activations_used"))
        });
    }
    catch (PostgresException pex) when (pex.SqlState == "23505")
    {
        return Results.Conflict(new { error = "Esa license_key ya existe." });
    }
});

app.MapPut("/admin/api/licenses/{id:int}/active", async (int id, AdminSetActive req) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    const string sql = @"UPDATE licenses SET is_active=@a WHERE id=@id;";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("a", req.isActive);
    cmd.Parameters.AddWithValue("id", id);

    int n = await cmd.ExecuteNonQueryAsync();
    if (n <= 0) return Results.NotFound(new { error = "No existe." });

    return Results.Ok(new { ok = true });
});

app.MapPost("/admin/api/licenses/{id:int}/reset", async (int id) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    const string sql = @"
UPDATE licenses
SET bound_machine_id = NULL,
    activations_used = 0
WHERE id = @id;";

    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("id", id);

    int n = await cmd.ExecuteNonQueryAsync();
    if (n <= 0) return Results.NotFound(new { error = "No existe." });

    return Results.Ok(new { ok = true });
});

app.MapPut("/admin/api/licenses/{id:int}/expires", async (int id, AdminSetExpires req) =>
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    const string sql = @"UPDATE licenses SET expires_at=@e WHERE id=@id;";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("id", id);

    if (req.expiresUtc == null) cmd.Parameters.AddWithValue("e", DBNull.Value);
    else cmd.Parameters.AddWithValue("e", req.expiresUtc.Value);

    int n = await cmd.ExecuteNonQueryAsync();
    if (n <= 0) return Results.NotFound(new { error = "No existe." });

    return Results.Ok(new { ok = true });
});

app.Run();

static string BuildNpgsqlConnStr(string raw)
{
    raw = raw.Trim();

    if (raw.Contains("Host=", StringComparison.OrdinalIgnoreCase))
        return EnsureSslMode(raw);

    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        raw = "postgresql://" + raw.Substring("postgres://".Length);

    if (raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);

        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

        var db = uri.AbsolutePath.TrimStart('/');

        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = db,
            Username = user,
            Password = pass,
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };

        return csb.ConnectionString;
    }

    return EnsureSslMode(raw);
}

static string EnsureSslMode(string cs)
{
    if (!cs.Contains("Ssl Mode", StringComparison.OrdinalIgnoreCase) &&
        !cs.Contains("SSL Mode", StringComparison.OrdinalIgnoreCase) &&
        !cs.Contains("sslmode", StringComparison.OrdinalIgnoreCase))
    {
        cs = cs.Trim().TrimEnd(';') + ";SSL Mode=Require;Trust Server Certificate=true;";
    }
    return cs;
}

static async Task EnsureSchemaAsync(string connStr)
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    const string sql = @"
CREATE TABLE IF NOT EXISTS licenses (
  id SERIAL PRIMARY KEY,
  license_key TEXT UNIQUE NOT NULL,
  is_active BOOLEAN NOT NULL DEFAULT TRUE,
  expires_at TIMESTAMPTZ NULL,
  bound_machine_id TEXT NULL,
  max_activations INT NOT NULL DEFAULT 1,
  activations_used INT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_licenses_key ON licenses (license_key);
CREATE INDEX IF NOT EXISTS idx_licenses_machine ON licenses (bound_machine_id);";

    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

record ValidateRequest(string licenseKey, string machineId);
record AdminCreateLicense(string licenseKey, bool isActive, DateTime? expiresUtc, int maxActivations);
record AdminSetActive(bool isActive);
record AdminSetExpires(DateTime? expiresUtc);