using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;

var builder = WebApplication.CreateBuilder(args);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger (solo dev)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// ✅ IMPORTANTE: servir wwwroot (admin panel)
app.UseStaticFiles();

// ✅ Redirect cómodo
app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));

// ✅ Render a veces pega HEAD al / (healthcheck). Soportamos GET y HEAD.
app.MapMethods("/", new[] { "GET", "HEAD" }, () => Results.Ok("POS Licensing API OK"));

// ✅ Lee connection string desde Render env var:
// KEY = ConnectionStrings__LicensingDb
string connStr =
    builder.Configuration.GetConnectionString("LicensingDb")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__LicensingDb")
    ?? "";

// Si no está configurada, falla claro (mejor que un 500 raro)
if (string.IsNullOrWhiteSpace(connStr))
{
    app.MapGet("/health/db", () => Results.Problem("Falta ConnectionStrings__LicensingDb en Render (connection string completa)."));
    app.Run();
    return;
}

// ✅ endpoint para revisar DB rápido
app.MapGet("/health/db", async () =>
{
    try
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
        var v = await cmd.ExecuteScalarAsync();
        return Results.Ok(new { ok = true, value = v });
    }
    catch (Exception ex)
    {
        return Results.Problem("DB error: " + ex.Message);
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
WHERE license_key = @k AND (bound_machine_id IS NULL OR bound_machine_id = '');";

            await using var cmdUp = new NpgsqlCommand(up, conn);
            cmdUp.Parameters.AddWithValue("m", req.machineId.Trim());
            cmdUp.Parameters.AddWithValue("k", req.licenseKey.Trim());
            await cmdUp.ExecuteNonQueryAsync();
        }

        return Results.Ok(new
        {
            valid = true,
            expiresUtc = expiresAt.HasValue ? DateTime.SpecifyKind(expiresAt.Value, DateTimeKind.Utc) : (DateTime?)null,
            message = "OK"
        });
    }
    catch (PostgresException pex)
    {
        // errores claros cuando falta tabla, schema, etc.
        return Results.Problem("Postgres error: " + pex.MessageText);
    }
    catch (Exception ex)
    {
        return Results.Problem("Server error: " + ex.Message);
    }
});

app.Run();

record ValidateRequest(string licenseKey, string machineId);