using Microsoft.OpenApi.Models;
using System.Data;
using System.Collections.Generic;
using Npgsql;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;

// Helper to convert DATABASE_URL (Heroku/Neon style) to Npgsql connection string
static string ConvertDatabaseUrlToConnectionString(string databaseUrl)
{
    // example: postgresql://user:pass@host:port/dbname?sslmode=require&channel_binding=require
    try
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':');
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port,
            Username = userInfo.Length > 0 ? userInfo[0] : string.Empty,
            Password = userInfo.Length > 1 ? userInfo[1] : string.Empty,
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };
        // parse query using QueryHelpers
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (query.TryGetValue("sslmode", out var val) && val.Count > 0 && val[0] == "disable")
        {
            builder.SslMode = SslMode.Disable;
        }
        return builder.ToString();
    }
    catch
    {
        return databaseUrl; // fallback: maybe already a connection string
    }
}

var builder = WebApplication.CreateBuilder(args);

// Configuration
var connStringEnv = Environment.GetEnvironmentVariable("DATABASE_URL");
var connString = !string.IsNullOrWhiteSpace(connStringEnv)
    ? ConvertDatabaseUrlToConnectionString(connStringEnv)
    : builder.Configuration.GetConnectionString("DefaultConnection") ?? string.Empty;

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Datamarket API", Version = "v1" });
});

builder.Services.AddScoped<IDbConnection>(sp => new NpgsqlConnection(connString));

var app = builder.Build();

// Allow Railway/containers PORT
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port) && int.TryParse(port, out var p))
{
    app.Urls.Clear();
    app.Urls.Add($"http://*:{p}");
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseHttpsRedirection();

// Endpoints: Productos (CRUD)
app.MapGet("/api/products", async (IDbConnection db) =>
{
    var sql = "SELECT * FROM productos ORDER BY producto_id";
    var items = await db.QueryAsync(sql);
    return Results.Ok(items);
});

app.MapGet("/api/products/{id}", async (int id, IDbConnection db) =>
{
    var item = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM productos WHERE producto_id=@Id", new { Id = id });
    return item is null ? Results.NotFound() : Results.Ok(item);
});

app.MapPost("/api/products", async (ProductDto product, IDbConnection db) =>
{
    var sql = "INSERT INTO productos (nombre, descripcion, precio, sku) VALUES (@Nombre,@Descripcion,@Precio,@Sku) RETURNING *";
    var created = await db.QuerySingleAsync<dynamic>(sql, product);
    return Results.Created($"/api/products/{created.producto_id}", created);
});

app.MapPut("/api/products/{id}", async (int id, ProductDto product, IDbConnection db) =>
{
    var sql = "UPDATE productos SET nombre=@Nombre, descripcion=@Descripcion, precio=@Precio, sku=@Sku WHERE producto_id=@Id RETURNING *";
    var updated = await db.QuerySingleOrDefaultAsync<dynamic>(sql, new { product.Nombre, product.Descripcion, product.Precio, product.Sku, Id = id });
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

app.MapDelete("/api/products/{id}", async (int id, IDbConnection db) =>
{
    await db.ExecuteAsync("DELETE FROM productos WHERE producto_id=@Id", new { Id = id });
    return Results.NoContent();
});

// Inventario
app.MapGet("/api/inventory", async (IDbConnection db) =>
{
    var rows = await db.QueryAsync("SELECT i.*, p.nombre FROM inventario i JOIN productos p ON p.producto_id=i.producto_id");
    return Results.Ok(rows);
});

app.MapGet("/api/inventory/{productId}", async (int productId, IDbConnection db) =>
{
    var row = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM inventario WHERE producto_id=@Id", new { Id = productId });
    return row is null ? Results.NotFound() : Results.Ok(row);
});

app.MapPut("/api/inventory/{productId}", async (int productId, InventoryDto dto, IDbConnection db) =>
{
    var sql = "UPDATE inventario SET cantidad_disponible=@Cantidad, punto_reorden=@Punto, ultima_actualizacion=NOW() WHERE producto_id=@Id RETURNING *";
    var updated = await db.QuerySingleOrDefaultAsync<dynamic>(sql, new { Cantidad = dto.CantidadDisponible, Punto = dto.PuntoReorden, Id = productId });
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

// Pedidos (crear con transacción)
app.MapGet("/api/pedidos", async (IDbConnection db) =>
{
    var rows = await db.QueryAsync("SELECT * FROM pedidos ORDER BY pedido_id DESC");
    return Results.Ok(rows);
});

app.MapGet("/api/pedidos/{id}", async (int id, IDbConnection db) =>
{
    var pedido = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM pedidos WHERE pedido_id=@Id", new { Id = id });
    var detalle = await db.QueryAsync("SELECT dp.*, p.nombre FROM detalle_pedidos dp JOIN productos p ON p.producto_id=dp.producto_id WHERE dp.pedido_id=@Id", new { Id = id });
    return Results.Ok(new { pedido, detalle });
});

app.MapPost("/api/pedidos", async (CreatePedidoDto dto, IDbConnection db) =>
{
    // Use connection and manual transaction because Dapper's extension requires the underlying NpgsqlConnection
    if (db is NpgsqlConnection conn)
    {
        await conn.OpenAsync();
        using var tran = await conn.BeginTransactionAsync();
        try
        {
            var pedido = await conn.QuerySingleAsync<dynamic>("INSERT INTO pedidos (cliente_nombre) VALUES (@Cliente) RETURNING *", new { Cliente = dto.ClienteNombre }, tran);
            decimal total = 0;
            foreach (var it in dto.Items)
            {
                var prod = await conn.QuerySingleOrDefaultAsync<dynamic>("SELECT precio FROM productos WHERE producto_id=@Id", new { Id = it.ProductoId }, tran);
                if (prod == null) throw new Exception("Producto no encontrado: " + it.ProductoId);
                decimal precio = (decimal)prod.precio;
                var subtotal = precio * it.Cantidad;
                total += subtotal;
                await conn.ExecuteAsync("INSERT INTO detalle_pedidos (pedido_id, producto_id, cantidad, precio_unitario_historico) VALUES (@Pedido,@Producto,@Cantidad,@Precio)", new { Pedido = pedido.pedido_id, Producto = it.ProductoId, Cantidad = it.Cantidad, Precio = precio }, tran);
                var inv = await conn.QuerySingleOrDefaultAsync<dynamic>("SELECT cantidad_disponible FROM inventario WHERE producto_id=@Id FOR UPDATE", new { Id = it.ProductoId }, tran);
                int available = 0;
                if (inv != null)
                {
                    try { available = Convert.ToInt32(inv.cantidad_disponible); } catch { available = 0; }
                }
                if (available < it.Cantidad) throw new Exception("Stock insuficiente para producto " + it.ProductoId);
                await conn.ExecuteAsync("UPDATE inventario SET cantidad_disponible = cantidad_disponible - @Qty, ultima_actualizacion = NOW() WHERE producto_id=@Id", new { Qty = it.Cantidad, Id = it.ProductoId }, tran);
            }
            await conn.ExecuteAsync("UPDATE pedidos SET total_pedido=@Total, estado=@Estado WHERE pedido_id=@Id", new { Total = total, Estado = "Procesando", Id = pedido.pedido_id }, tran);
            await tran.CommitAsync();
            return Results.Created($"/api/pedidos/{pedido.pedido_id}", new { pedido_id = pedido.pedido_id, total });
        }
        catch (Exception ex)
        {
            await tran.RollbackAsync();
            return Results.BadRequest(new { error = ex.Message });
        }
    }
    return Results.StatusCode(500);
});

app.Run();

// DTOs
public record ProductDto(string Nombre, string? Descripcion, decimal Precio, string Sku);
public record InventoryDto(int CantidadDisponible, int PuntoReorden);
public record CreatePedidoDto(string ClienteNombre, List<ItemDto> Items);
public record ItemDto(int ProductoId, int Cantidad);
