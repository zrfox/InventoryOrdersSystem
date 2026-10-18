# Inventory & Orders System

Two ASP.NET Core microservices (**InventoryService**, **OrdersService**) backed by SQL Server,
plus a small **Frontend** web app. The services never reference each other's code;
OrdersService talks to InventoryService over HTTP (one-way: Orders -> Inventory), and the
Frontend talks to both through a built-in reverse proxy that holds the API key server-side.

