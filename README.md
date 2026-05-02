API .NET Minimal para Datamarket

Para ejecutar:
1. Instalar .NET 7 SDK
2. Desde la carpeta `backend` ejecutar: dotnet restore; dotnet run
3. La API se expondrá en https://localhost:5001 (o el puerto asignado)

La cadena de conexión está en `appsettings.json`. Puedes también usar variable de entorno `DATABASE_URL`.

Endpoints:
- GET /api/products
- GET /api/products/{id}
- POST /api/products
- PUT /api/products/{id}
- DELETE /api/products/{id}
- GET /api/inventory
- PUT /api/inventory/{productId}
- GET /api/pedidos
- GET /api/pedidos/{id}
- POST /api/pedidos
