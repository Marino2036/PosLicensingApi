using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using System.Globalization;

namespace PosLicensingApi
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Swagger
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            // JSON estable
            builder.Services.Configure<JsonOptions>(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = null;
            });

            var app = builder.Build();

            // Swagger (solo dev)
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // ✅ Sirve wwwroot (admin panel)
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // ✅ Redirect cómodo
            app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));

            // ✅ Render a veces pega HEAD al / (healthcheck). Soportamos GET y HEAD.
            app.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok("POS Licensing API OK"));

            // ✅ Connection String (Render env var recomendado)
            string connStr =
                builder.Configuration.GetConnectionString("LicensingDb")
                ?? Environment.GetEnvironmentVariable("ConnectionStrings__LicensingDb")
                ?? "";

            // ✅ Admin key (Render env var: ADMIN_KEY)
            string adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? "";

            // Si no hay conn string, no seguimos
            if (string.IsNullOrWhiteSpace(connStr))
            {
                app.MapGet("/health/db", () => Results.Problem("Falta ConnectionStrings__LicensingDb en Render (connection string completa)."));
                app.Run();
                return;
            }

            // ✅ Asegurar tabla / schema al iniciar
            await EnsureSchemaAsync(connStr);

            // ✅ endpoint para revisar DB rápido
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
                        server_time = rd["server_time"]?.ToString()
                    });
                }
                catch (Exception ex)
                {
                    return Results.Problem("DB error: " + ex.Message);
                }
            });

            // ======================================================
            // ===============  LICENSING (POS) =====================
            // ======================================================
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

                    // Si no está ligada, ligar y subir activations_used
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

            // ======================================================
            // ===============  ADMIN PANEL API  =====================
            // ======================================================

            // Middleware simple de auth solo para /admin/api
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

            // GET /admin/api/licenses?q=
            app.MapGet("/admin/api/licenses", async (string? q) =>
            {
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync();

                q = (q ?? "").Trim();

                string sql;
                var cmd = new NpgsqlCommand();
                cmd.Connection = conn;

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
                    list.Add(new
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

                return Results.Ok(list);
            });

            // POST /admin/api/licenses
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

                if (req.expiresUtc == null)
                    cmd.Parameters.AddWithValue("e", DBNull.Value);
                else
                    cmd.Parameters.AddWithValue("e", req.expiresUtc.Value);

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

            // PUT /admin/api/licenses/{id}/active
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

            // POST /admin/api/licenses/{id}/reset
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

            // PUT /admin/api/licenses/{id}/expires
            app.MapPut("/admin/api/licenses/{id:int}/expires", async (int id, AdminSetExpires req) =>
            {
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync();

                const string sql = @"UPDATE licenses SET expires_at=@e WHERE id=@id;";
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("id", id);

                if (req.expiresUtc == null)
                    cmd.Parameters.AddWithValue("e", DBNull.Value);
                else
                    cmd.Parameters.AddWithValue("e", req.expiresUtc.Value);

                int n = await cmd.ExecuteNonQueryAsync();
                if (n <= 0) return Results.NotFound(new { error = "No existe." });

                return Results.Ok(new { ok = true });
            });

            app.Run();
        }

        private static async Task EnsureSchemaAsync(string connStr)
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
CREATE INDEX IF NOT EXISTS idx_licenses_machine ON licenses (bound_machine_id);
";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public record ValidateRequest(string licenseKey, string machineId);

    public record AdminCreateLicense(string licenseKey, bool isActive, DateTime? expiresUtc, int maxActivations);

    public record AdminSetActive(bool isActive);

    public record AdminSetExpires(DateTime? expiresUtc);
}