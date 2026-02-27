using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ✅ Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ✅ IMPORTANTE PARA RENDER: escuchar el puerto dinámico
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// ✅ Swagger (solo dev)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    // ✅ En local está bien redirigir a HTTPS
    app.UseHttpsRedirection();
}
// ⚠️ En Render NO pongas UseHttpsRedirection() fuera de Development
// porque Render maneja HTTPS por fuera (proxy) y tu app va por HTTP internamente.

// (Opcional) endpoint raíz para probar rápido
app.MapGet("/", () => Results.Ok("POS Licensing API OK"));


// ✅ CONEXIÓN A POSTGRES
// 1) Primero intenta ConnectionStrings:LicensingDb (ideal si la pones en Render)
string? connStr = builder.Configuration.GetConnectionString("LicensingDb");

// 2) Si no existe, intenta DATABASE_URL (Render Postgres)
if (string.IsNullOrWhiteSpace(connStr))
{
    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrWhiteSpace(databaseUrl))
    {
        // Ejemplo Render: postgres://user:pass@host:port/db
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);

        var username = userInfo.Length > 0 ? userInfo[0] : "";
        var password = userInfo.Length > 1 ? userInfo[1] : "";

        connStr =
            $"Host={uri.Host};" +
            $"Port={uri.Port};" +
            $"Database={uri.AbsolutePath.TrimStart('/')};" +
            $"Username={username};" +
            $"Password={password};" +
            $"SSL Mode=Require;Trust Server Certificate=true";
    }
}

// 3) Fallback local (solo para tu PC)
if (string.IsNullOrWhiteSpace(connStr))
{
    connStr = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=TU_PASSWORD";
}

app.MapPost("/license/validate", async (ValidateRequest req) =>
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

    string boundMachine = rd.IsDBNull(rd.GetOrdinal("bound_machine_id"))
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
});

app.Run();

record ValidateRequest(string licenseKey, string machineId);