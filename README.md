# Inventory & Orders

A small microservice system: **InventoryService** (products and stock), **OrdersService**
(orders) and a **Frontend** (web UI), backed by SQL Server. The services are separate
processes so each can be built, deployed and restarted on its own, and a problem in one is
isolated from the other. Calls are one-way: OrdersService calls InventoryService, never the
reverse. The Frontend calls both.

| App              | URL                              |
|------------------|----------------------------------|
| Frontend (UI)    | http://localhost:5211            |
| InventoryService | http://localhost:5209/swagger    |
| OrdersService    | http://localhost:5210/swagger    |

Swagger stays enabled in all environments because this is a demonstration project.

## Prerequisites

- Docker: Docker Desktop only.
- Local: .NET 8 SDK + SQL Server Express.
- Tests: .NET 8 SDK (no Docker or SQL Server required).

## Setup

1. `git clone <repo>` and `cd` into it.
2. Create `.env` at the solution root from `.env.example`.
   - `InventoryApiKey`: any string.
   - `MSSQL_SA_PASSWORD`: 8+ characters with 3 of uppercase / lowercase / digit / symbol (Docker only).
3. Local only: put your SQL Express connection strings in each service's `appsettings.json`.

## Run locally

Three terminals at the solution root:

```
cd InventoryService; dotnet run
cd OrdersService; dotnet run
cd Frontend; dotnet run
```

Open http://localhost:5211. Add products through the InventoryService Swagger page
(click **Authorize** and paste your `InventoryApiKey` first).

## Run with Docker

```
docker compose up -d --build
```

Four containers: `sqlserver`, `inventoryservice`, `ordersservice`, `frontend`.
Open http://localhost:5211.

## What the UI does

- Shows products with stock read from InventoryService. Stock is re-read after every action
  and every few seconds; the browser never calculates it.
- Places orders. The submit button is disabled while a request is in flight and each
  submission carries an `Idempotency-Key`, so clicking twice cannot create two orders.
- Cancels confirmed orders, which returns their stock to inventory.
- Errors (for example cancelling an order that is already cancelled or was rejected) appear
  as a pop-up in the UI and as an error line in the OrdersService terminal.

## Order statuses

| Status                | Meaning                                                                   |
|-----------------------|---------------------------------------------------------------------------|
| `Confirmed`           | Stock was available and has been deducted.                                |
| `Rejected`            | Not enough stock. Cannot be cancelled.                                    |
| `CancellationPending` | Cancel requested, but InventoryService could not confirm the stock return.|
| `Cancelled`           | Stock has been returned. Cannot be cancelled again.                       |

### CancellationPending

- OrdersService retries every 5 seconds, indefinitely, and immediately on startup. The state
  is stored in the Orders database, so it survives OrdersService restarts.
- At most 5 orders can be pending at once; a sixth cancel is refused (HTTP 429) until the
  queue drains.
- A pending order cannot be cancelled again, but the cancellation can be undone
  (`POST /api/orders/{id}/cancel/undo`), which returns it to `Confirmed`.
- Undo is refused if a stock-return request may already have reached InventoryService
  (for example it timed out rather than failing to connect). That cancellation completes
  automatically instead.
- Stock is returned at most once per order: each order has a `CancellationId`, and
  InventoryService ignores a `CancellationId` it has already applied.

Optional settings (OrdersService configuration section `Cancellation`): `MaxPending` (5),
`RetryIntervalSeconds` (5), `InventoryTimeoutSeconds` (10).

### Trying it out

- Docker: `docker compose stop inventoryservice`, cancel a confirmed order in the UI
  (it becomes CancellationPending), then `docker compose start inventoryservice`.
- Local: stop the InventoryService terminal with Ctrl+C, cancel an order, start it again.

## API additions

OrdersService
- `POST /api/orders` accepts an optional `Idempotency-Key` header.
- `POST /api/orders/{id}/cancel` → 200 cancelled, 202 pending, 404, 409, 429.
- `POST /api/orders/{id}/cancel/undo` → 200, 404, 409.

InventoryService
- `POST /api/products/{id}/stock/deduct` `{ quantity }` → 200, 404, 409 (insufficient stock).
- `POST /api/products/{id}/stock/restore` `{ operationId, quantity }` → 200 (idempotent), 404.

## Tests

```
dotnet test
```

- **OrdersService.Tests** (unit): order confirmed / rejected / product not found; duplicate
  submissions; cancel, double cancel, rejected cancel; CancellationPending, the limit of 5,
  perpetual retry, restart, undo, and stock never being returned twice.
- **IntegrationTests**: both services running in memory together — order placement and
  stock deduction, insufficient stock, cancellation returning stock, Inventory going down and
  coming back, OrdersService restarting with a pending cancellation, undo, the pending
  limit, and the idempotent Inventory stock endpoints.

## Notes

- The Frontend keeps the API key on the server and has no login of its own; anyone who can
  reach it can place and cancel orders.
- OrdersService is assumed to run as a single instance (its cancellation and
  duplicate-submission locks are in-process).
